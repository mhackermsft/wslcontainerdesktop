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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tests.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Runtime;

/// <summary>
/// Runs opt-in runtime checks against the real <c>wslc</c> preview CLI so service assumptions match engine behavior.
/// </summary>
public sealed class Wslc301RuntimeTests
{
    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task NativeRestartRestartsRunningStartsStoppedAndAcceptsSignalTimeout()
    {
        await using var lease = new WslcRuntimeLease();
        await lease.InitializeAsync();
        var name = lease.Name("restart");
        var run = await lease.Service.RunContainerAsync(lease.Container(name, command: "sh -c \"sleep 300\""), lease.Ct);
        WslcRuntimeLease.Require(run, "run restart target");
        lease.TrackContainer(name);
        var before = await lease.InspectContainerJsonAsync(name);
        var beforeStarted = StartedAt(before);

        await Task.Delay(TimeSpan.FromSeconds(2), lease.Ct);
        WslcRuntimeLease.Require(await lease.Service.RestartContainerAsync(name, lease.Ct), "restart running target");
        var after = await lease.WaitForContainerStateAsync(name, "running");
        Assert.True(StartedAt(after) > beforeStarted);

        WslcRuntimeLease.Require(await lease.Service.StopContainerAsync(name, lease.Ct), "stop before stopped restart");
        await lease.WaitForContainerStateAsync(name, "exited", "stopped");
        WslcRuntimeLease.Require(await lease.Service.RestartContainerAsync(name, timeSeconds: 1, signal: "SIGTERM", lease.Ct),
            "restart stopped target with signal and timeout");
        var restarted = await lease.WaitForContainerStateAsync(name, "running");
        Assert.Equal("running", StateStatus(restarted));
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task StopTimeoutCreateOptionPersistsInInspectConfig()
    {
        await using var lease = new WslcRuntimeLease();
        await lease.InitializeAsync();
        foreach (var timeout in new[] { 7, -1 })
        {
            var name = lease.Name("stop-timeout-" + (timeout < 0 ? "minus1" : timeout.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var options = lease.Container(name, command: "sh -c \"sleep 300\"");
            options.StopTimeoutSeconds = timeout;
            WslcRuntimeLease.Require(await lease.Service.CreateContainerAsync(options, lease.Ct), "create stop-timeout target");
            lease.TrackContainer(name);
            var inspect = await lease.InspectContainerJsonAsync(name);
            Assert.Equal(timeout, inspect.GetProperty("Config").GetProperty("StopTimeout").GetInt32());
        }
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task MountsBindVolumeAndTmpfsAndReadonlyBindRejectsWrites()
    {
        await using var lease = new WslcRuntimeLease();
        await lease.InitializeAsync();
        var host = lease.CreateDirectory("bind");
        await File.WriteAllTextAsync(Path.Combine(host, lease.Name("bind-file") + ".txt"), "host", lease.Ct);
        var volume = lease.Name("mount-vol");
        lease.TrackVolume(volume);
        var name = lease.Name("mounts");
        var options = lease.Container(name, command: "sh -c \"sleep 300\"");
        options.Mounts.AddRange([
            new RunContainerMount { Type = "bind", Source = host, Target = "/mnt/bind", ReadOnly = true },
            new RunContainerMount { Type = "volume", Source = volume, Target = "/mnt/volume" },
            new RunContainerMount { Type = "tmpfs", Target = "/mnt/tmpfs" },
        ]);

        WslcRuntimeLease.Require(await lease.Service.RunContainerAsync(options, lease.Ct), "run mount target");
        lease.TrackContainer(name);
        var inspect = await lease.InspectContainerJsonAsync(name);
        var mounts = inspect.GetProperty("Mounts").EnumerateArray().ToArray();
        Assert.Contains(mounts, m => HasMount(m, "bind", "/mnt/bind", readOnly: true));
        Assert.Contains(mounts, m => HasMount(m, "volume", "/mnt/volume", nameOrSource: volume));
        Assert.Contains(mounts, m => HasMount(m, "tmpfs", "/mnt/tmpfs"));
        var write = await lease.Service.ExecAsync(name, "touch /mnt/bind/should-not-write", lease.Ct);
        Assert.False(write.Success, write.StandardOutput + write.StandardError);
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task NetworkCreateConnectDisconnectPersistsAliasesStaticIpLabelsAndOptions()
    {
        await using var lease = new WslcRuntimeLease();
        await lease.InitializeAsync();
        var octet = Random.Shared.Next(40, 220);
        var network = lease.Name("net");
        lease.TrackNetwork(network);
        var labels = new Dictionary<string, string> { ["wslcd-rt-owner"] = lease.Suffix };
        WslcRuntimeLease.Require(await lease.Service.CreateNetworkAsync(
            network,
            driver: "bridge",
            driverOpts: ["com.docker.network.driver.mtu=1450"],
            labels: labels,
            subnet: $"10.{octet}.0.0/24",
            gateway: $"10.{octet}.0.1",
            ipRange: null,
            ct: lease.Ct,
            internalNetwork: true), "create network");
        var networkInspect = await lease.InspectNetworkJsonAsync(network);
        Assert.True(networkInspect.GetProperty("Internal").GetBoolean());
        Assert.Equal("1450", networkInspect.GetProperty("Options").GetProperty("com.docker.network.driver.mtu").GetString());
        Assert.Equal(lease.Suffix, networkInspect.GetProperty("Labels").GetProperty("wslcd-rt-owner").GetString());

        var container = lease.Name("net-ctr");
        WslcRuntimeLease.Require(await lease.Service.RunContainerAsync(lease.Container(container, command: "sh -c \"sleep 300\""), lease.Ct),
            "run network target");
        lease.TrackContainer(container);
        var endpoint = new NetworkAttachment
        {
            Network = network,
            Ipv4Address = $"10.{octet}.0.10",
            Aliases = [lease.Name("alias")],
        };
        WslcRuntimeLease.Require(await lease.Service.ConnectNetworkAsync(endpoint, container, lease.Ct), "connect network");
        var connected = await lease.InspectContainerJsonAsync(container);
        var attachment = connected.GetProperty("NetworkSettings").GetProperty("Networks").GetProperty(network);
        Assert.Equal(endpoint.Ipv4Address, attachment.GetProperty("IPAddress").GetString());
        Assert.True(JsonContainsString(attachment, endpoint.Aliases[0]));

        WslcRuntimeLease.Require(await lease.Service.DisconnectNetworkAsync(network, container, lease.Ct), "disconnect network");
        var disconnected = await lease.InspectContainerJsonAsync(container);
        Assert.False(disconnected.GetProperty("NetworkSettings").GetProperty("Networks").TryGetProperty(network, out _));
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task EngineEventStreamReconnectsWithoutDuplicateOrMissingContainerEvents()
    {
        await using var lease = new WslcRuntimeLease();
        await lease.InitializeAsync();
        using var stream = new EngineEventStream(
            lease.Settings,
            new StaticRequirementService(WslRequirementStatus.Ok("3.0.1.0")),
            action => { action(); return true; },
            NullLogger<EngineEventStream>.Instance);
        var events = new List<EngineEvent>();
        stream.EventReceived += (_, evt) =>
        {
            if (evt.Attribute("name")?.StartsWith(WslcRuntimeLease.Prefix + lease.Suffix, StringComparison.Ordinal) == true)
            {
                lock (events) events.Add(evt);
            }
        };
        stream.Start();
        await SpinUntilAsync(() => stream.IsConnected, lease.Ct);

        var name = lease.Name("events");
        WslcRuntimeLease.Require(await lease.Service.CreateContainerAsync(lease.Container(name, command: "sh -c \"sleep 300\""), lease.Ct),
            "create event target");
        lease.TrackContainer(name);
        WslcRuntimeLease.Require(await lease.Service.StartContainerAsync(name, lease.Ct), "start event target");
        await SpinUntilAsync(() => Snapshot(events).Any(e => e.IsAction("start") && e.Attribute("name") == name), lease.Ct);
        stream.Restart();
        WslcRuntimeLease.Require(await lease.Service.StopContainerAsync(name, lease.Ct), "stop event target");
        WslcRuntimeLease.Require(await lease.Service.RemoveContainerAsync(name, force: true, lease.Ct), "remove event target");
        lease.UntrackContainer(name);

        await SpinUntilAsync(() => Snapshot(events).Any(e => e.IsAction("destroy") && e.Attribute("name") == name), lease.Ct);
        var observed = Snapshot(events).Where(e => e.Attribute("name") == name).ToArray();
        Assert.Equal(observed.Length, observed.Select(e => e.StableKey).Distinct(StringComparer.Ordinal).Count());
        AssertOrdered(observed, "create", "start", "stop", "destroy");
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task NativeCopyTransfersFilesForStoppedContainersAndFollowsSymlinkDownloads()
    {
        await using var lease = new WslcRuntimeLease();
        await lease.InitializeAsync();
        var name = lease.Name("copy");
        WslcRuntimeLease.Require(await lease.Service.CreateContainerAsync(lease.Container(name, command: "sh -c \"sleep 300\""), lease.Ct),
            "create copy target");
        lease.TrackContainer(name);
        var sourceDir = lease.CreateDirectory("copy-source");
        var sourceFile = Path.Combine(sourceDir, lease.Name("payload") + ".txt");
        await File.WriteAllTextAsync(sourceFile, "copy-content", new UTF8Encoding(false), lease.Ct);

        WslcRuntimeLease.Require(await lease.Service.CopyToContainerAsync(name, sourceFile, "/tmp", lease.Ct), "copy into stopped container");
        WslcRuntimeLease.Require(await lease.Service.StartContainerAsync(name, lease.Ct), "start copy target");
        var verifyCopy = await lease.Service.ExecAsync(name, $"test \"$(cat /tmp/{Path.GetFileName(sourceFile)})\" = copy-content", lease.Ct);
        if (!verifyCopy.Success)
        {
            var listing = await lease.Service.ExecAsync(name, "find /tmp -maxdepth 3 -type f -print -exec cat {} \\;", lease.Ct);
            Assert.Fail(verifyCopy.ErrorText + Environment.NewLine + listing.StandardOutput + listing.StandardError);
        }
        var linkName = lease.Name("payload-link");
        WslcRuntimeLease.Require(await lease.Service.ExecAsync(name, $"ln -sf /tmp/{Path.GetFileName(sourceFile)} /tmp/{linkName}", lease.Ct),
            "create symlink");
        WslcRuntimeLease.Require(await lease.Service.StopContainerAsync(name, lease.Ct), "stop copy target");
        var download = lease.CreateDirectory("copy-download");
        WslcRuntimeLease.Require(await lease.Service.CopyFromContainerAsync(name, "/tmp/" + linkName, download, lease.Ct, followSymlinks: true),
            "copy followed symlink out of stopped container");
        Assert.Equal("copy-content", (await File.ReadAllTextAsync(Path.Combine(download, linkName), lease.Ct)).TrimStart('\uFEFF'));
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task ImageSaveLoadAndContainerExportImportRoundTripOwnedTagsOnly()
    {
        await using var lease = new WslcRuntimeLease();
        await lease.InitializeAsync();
        var copyRef = lease.Name("copy") + ":latest";
        lease.TrackImage(copyRef);
        WslcRuntimeLease.Require(await lease.Service.TagImageAsync(WslcRuntimeLease.Image, copyRef, lease.Ct), "tag copy image");
        var imageTar = Path.Combine(lease.CreateDirectory("image-save"), lease.Name("image") + ".tar");
        WslcRuntimeLease.Require(await lease.Service.SaveImagesAsync([copyRef], imageTar, lease.Ct), "save image");
        WslcRuntimeLease.Require(await lease.Service.RemoveImageAsync(copyRef, force: true, lease.Ct), "remove saved copy tag");
        WslcRuntimeLease.Require(await lease.Service.LoadImageAsync(imageTar, lease.Ct), "load saved image");
        Assert.Contains(await lease.Service.ListImagesAsync(lease.Ct, showAll: true), i => i.Repository == lease.Repository(copyRef) && i.Tag == "latest");

        var container = lease.Name("export");
        WslcRuntimeLease.Require(await lease.Service.CreateContainerAsync(lease.Container(container, command: "sh -c \"echo exported > /wslcd-exported\""), lease.Ct),
            "create export target");
        lease.TrackContainer(container);
        var exportTar = Path.Combine(lease.CreateDirectory("container-export"), lease.Name("container") + ".tar");
        WslcRuntimeLease.Require(await lease.Service.ExportContainerAsync(container, exportTar, lease.Ct), "export container");
        var importedRef = lease.Name("imported") + ":latest";
        lease.TrackImage(importedRef);
        WslcRuntimeLease.Require(await lease.Service.ImportImageAsync(exportTar, importedRef, lease.Ct), "import container filesystem");
        Assert.Contains(await lease.Service.ListImagesAsync(lease.Ct, showAll: true), i => i.Repository == lease.Repository(importedRef) && i.Tag == "latest");
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task ListingParityIncludesDigestsAllSizesInspectSizeAndSystemInfo()
    {
        await using var lease = new WslcRuntimeLease();
        await lease.InitializeAsync();
        var name = lease.Name("listing");
        WslcRuntimeLease.Require(await lease.Service.CreateContainerAsync(lease.Container(name, command: "sh -c \"sleep 300\""), lease.Ct),
            "create listing target");
        lease.TrackContainer(name);

        var images = await lease.Service.ListImagesAsync(lease.Ct);
        Assert.NotEmpty(images);
        // Digest defaults to empty, so require at least one pulled image to have a parsed digest.
        Assert.Contains(images, image => image.HasDigest && image.Digest.StartsWith("sha256:", StringComparison.Ordinal));
        var allImages = await lease.Service.ListImagesAsync(lease.Ct, showAll: true);
        Assert.True(allImages.Count >= images.Count);

        var defaultContainers = await lease.Service.ListContainersAsync(all: false, lease.Ct);
        var allContainers = await lease.Service.ListContainersAsync(all: true, lease.Ct);
        Assert.True(allContainers.Count >= defaultContainers.Count);
        var sized = await lease.Service.ListContainersAsync(all: true, lease.Ct, includeSize: true);
        var row = Assert.Single(sized, c => c.Name == name);
        Assert.True(row.SizeKnown, "list --size did not populate size information.");
        Assert.True(row.SizeRwBytes is not null || row.SizeRootFsBytes is not null, "list --size did not parse writable/virtual bytes.");

        var inspect = await lease.InspectContainerJsonAsync(name, includeSize: true);
        Assert.True(inspect.GetProperty("SizeRw").GetInt64() >= 0);
        Assert.True(inspect.GetProperty("SizeRootFs").GetInt64() > 0);
        var info = await lease.Service.GetSystemInfoAsync(lease.Ct);
        Assert.StartsWith("3.0.1", info.Client.Version, StringComparison.Ordinal);
        Assert.NotEmpty(info.Server.Sessions);
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task LogsWithDetailsAndTimestampsSucceedAndIncludeTimestampedOutput()
    {
        await using var lease = new WslcRuntimeLease();
        await lease.InitializeAsync();
        var name = lease.Name("logs");
        var options = lease.Container(name, command: "sh -c \"echo wslcd-runtime-log; sleep 300\"");
        options.Labels["wslcd-rt-log"] = lease.Suffix;
        WslcRuntimeLease.Require(await lease.Service.RunContainerAsync(options, lease.Ct), "run log target");
        lease.TrackContainer(name);
        await SpinUntilAsync(async () => (await lease.Service.GetLogsAsync(name, tail: 20, lease.Ct, details: true, timestamps: true)).StandardOutput.Contains("wslcd-runtime-log", StringComparison.Ordinal), lease.Ct);
        var logs = await lease.Service.GetLogsAsync(name, tail: 20, lease.Ct, details: true, timestamps: true);
        WslcRuntimeLease.Require(logs, "logs with details and timestamps");
        var line = logs.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).First(l => l.Contains("wslcd-runtime-log", StringComparison.Ordinal));
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T", line);
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task ComposeImporterCreatePathCoversStopGraceMountAndMultipleNetworks()
    {
        await using var lease = new WslcRuntimeLease();
        await lease.InitializeAsync();
        var a = lease.Name("compose-a");
        var b = lease.Name("compose-b");
        var volume = lease.Name("compose-vol");
        lease.TrackNetwork(a);
        lease.TrackNetwork(b);
        lease.TrackVolume(volume);
        var octet = Random.Shared.Next(40, 200);
        var project = ComposeImporter.ParseProject($$"""
            services:
              web:
                image: nginx:alpine
                command: [sh, -c, "sleep 300"]
                stop_grace_period: 7s
                volumes:
                  - type: volume
                    source: {{volume}}
                    target: /compose-volume
                networks:
                  {{a}}:
                    aliases: [web-a]
                    ipv4_address: 10.{{octet}}.10.10
                  {{b}}:
                    aliases: [web-b]
                    ipv4_address: 10.{{octet}}.11.10
            networks:
              {{a}}:
                name: {{a}}
                driver: bridge
                internal: true
                driver_opts:
                  com.docker.network.driver.mtu: "1450"
                labels:
                  wslcd-rt-owner: {{lease.Suffix}}
                ipam:
                  config:
                    - subnet: 10.{{octet}}.10.0/24
                      gateway: 10.{{octet}}.10.1
              {{b}}:
                name: {{b}}
                driver: bridge
                internal: true
                labels:
                  wslcd-rt-owner: {{lease.Suffix}}
                ipam:
                  config:
                    - subnet: 10.{{octet}}.11.0/24
                      gateway: 10.{{octet}}.11.1
            volumes:
              {{volume}}: {}
            """);
        Assert.Equal(2, project.Networks.Count);
        foreach (var network in project.Networks)
        {
            WslcRuntimeLease.Require(await lease.Service.CreateNetworkAsync(
                network.ExplicitName ?? network.Name,
                network.Driver,
                network.DriverOpts,
                network.Labels,
                network.Subnet,
                network.Gateway,
                network.IpRange,
                lease.Ct,
                network.Internal), "create compose network");
        }

        var service = Assert.Single(project.Services);
        Assert.Equal(7, service.Options.StopTimeoutSeconds);
        Assert.Contains(service.Options.Mounts, m => m.Type == "volume" && m.Source == volume && m.Target == "/compose-volume");
        var attachments = service.Options.GetNetworkAttachments();
        Assert.Equal(2, attachments.Count);
        service.Options.Name = lease.Name("compose-web");
        WslcRuntimeLease.Require(await lease.Service.RunContainerAsync(service.Options, lease.Ct), "run compose-imported service");
        lease.TrackContainer(service.Options.Name);
        foreach (var endpoint in attachments.Skip(1))
        {
            WslcRuntimeLease.Require(await lease.Service.ConnectNetworkAsync(endpoint, service.Options.Name, lease.Ct), "connect secondary compose network");
        }

        var inspect = await lease.InspectContainerJsonAsync(service.Options.Name);
        Assert.Equal(7, inspect.GetProperty("Config").GetProperty("StopTimeout").GetInt32());
        Assert.Contains(inspect.GetProperty("Mounts").EnumerateArray(), m => HasMount(m, "volume", "/compose-volume", nameOrSource: volume));
        var networks = inspect.GetProperty("NetworkSettings").GetProperty("Networks");
        Assert.True(networks.TryGetProperty(a, out _));
        Assert.True(networks.TryGetProperty(b, out _));
        var aInspect = await lease.InspectNetworkJsonAsync(a);
        Assert.True(aInspect.GetProperty("Internal").GetBoolean());
        Assert.Equal("1450", aInspect.GetProperty("Options").GetProperty("com.docker.network.driver.mtu").GetString());
        Assert.Equal(lease.Suffix, aInspect.GetProperty("Labels").GetProperty("wslcd-rt-owner").GetString());
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task SettingsFileServiceUpdatesTempCopyOnlyAndPreservesUtf8WithoutBom()
    {
        await using var lease = new WslcRuntimeLease();
        await lease.InitializeAsync();
        var info = await lease.Service.GetSystemInfoAsync(lease.Ct);
        var realText = File.Exists(info.Client.SettingsFile)
            ? await File.ReadAllTextAsync(info.Client.SettingsFile, lease.Ct)
            : "credentialStore: default\nsession:\n  storagePath: default\n";
        var settingsDir = lease.CreateDirectory("settings-copy");
        var settingsFile = Path.Combine(settingsDir, lease.Name("settings") + ".yaml");
        await File.WriteAllTextAsync(settingsFile, realText, new UTF8Encoding(false), lease.Ct);
        var targetStorage = Path.Combine(settingsDir, lease.Name("storage"));
        var service = new WslcSettingsFileService(
            NetworkTestProxy.Create<IWslcService>((method, _) => method.Name == nameof(IWslcService.GetSystemInfoAsync)
                ? Task.FromResult(new WslcSystemInfo { Client = new WslcClientInfo { SettingsFile = settingsFile } })
                : throw new InvalidOperationException(method.Name)),
            NullLogger<WslcSettingsFileService>.Instance);

        await service.SetStoragePathAsync(targetStorage, lease.Ct);

        Assert.True(File.Exists(settingsFile));
        var bytes = await File.ReadAllBytesAsync(settingsFile, lease.Ct);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var parsed = await service.ReadAsync(lease.Ct);
        Assert.Equal(targetStorage, parsed.StoragePath);
        Assert.Single(Directory.GetFiles(settingsDir, "*.yaml"));
    }

    [WslcRuntimeFact]
    [Trait("Category", "WslcRuntime")]
    public async Task RequirementServiceReportsRealEngineOkAndMissingPathNotInstalled()
    {
        await using var lease = new WslcRuntimeLease();
        using var okSettings = new RuntimeSettings(WslcRuntimeLease.WslcPath);
        using var missingSettings = new RuntimeSettings(Path.Combine(Environment.CurrentDirectory, lease.Name("missing"), "wslc.exe"));
        using var ok = new WslRequirementService(
            okSettings,
            AllowAllPolicy.Instance,
            null,
            NullLogger<WslRequirementService>.Instance,
            ProcessRunner.RunAtPathAsync,
            (_, _) => Task.CompletedTask);
        using var missing = new WslRequirementService(
            missingSettings,
            AllowAllPolicy.Instance,
            null,
            NullLogger<WslRequirementService>.Instance,
            ProcessRunner.RunAtPathAsync,
            (_, _) => Task.CompletedTask);

        var okStatus = await ok.RecheckAsync(lease.Ct);
        Assert.Equal(WslRequirementState.Ok, okStatus.State);
        Assert.NotNull(okStatus.FoundVersion);
        Assert.True(Version.Parse(okStatus.FoundVersion!) >= new Version(3, 0, 1));
        var missingStatus = await missing.RecheckAsync(lease.Ct);
        Assert.Equal(WslRequirementState.NotInstalled, missingStatus.State);
    }

    private static DateTimeOffset StartedAt(JsonElement inspect) =>
        DateTimeOffset.Parse(inspect.GetProperty("State").GetProperty("StartedAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    private static string StateStatus(JsonElement inspect)
    {
        var state = inspect.GetProperty("State");
        if (state.TryGetProperty("Status", out var status) && status.ValueKind == JsonValueKind.String)
        {
            return status.GetString()!.Equals("exited", StringComparison.OrdinalIgnoreCase) ? "stopped" : status.GetString()!.ToLowerInvariant();
        }

        return state.GetProperty("Running").GetBoolean() ? "running" : "stopped";
    }

    private static bool HasMount(JsonElement mount, string type, string destination, bool? readOnly = null, string? nameOrSource = null)
    {
        if (!mount.GetProperty("Type").GetString()!.Equals(type, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(mount.GetProperty("Destination").GetString(), destination, StringComparison.Ordinal)) return false;
        if (readOnly is not null)
        {
            var readWrite = mount.TryGetProperty("RW", out var rw)
                ? rw.GetBoolean()
                : mount.GetProperty("ReadWrite").GetBoolean();
            if (readWrite == readOnly.Value) return false;
        }
        return nameOrSource is null ||
            mount.TryGetProperty("Name", out var name) && name.GetString() == nameOrSource ||
            mount.TryGetProperty("Source", out var source) && source.GetString() == nameOrSource;
    }

    private static bool JsonContainsString(JsonElement element, string value)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => string.Equals(element.GetString(), value, StringComparison.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Any(item => JsonContainsString(item, value)),
            JsonValueKind.Object => element.EnumerateObject().Any(property => JsonContainsString(property.Value, value)),
            _ => false,
        };
    }

    private static async Task SpinUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not reached.");
            }

            await Task.Delay(100, ct);
        }
    }

    private static async Task SpinUntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not reached.");
            }

            await Task.Delay(100, ct);
        }
    }

    private static EngineEvent[] Snapshot(List<EngineEvent> events)
    {
        lock (events) return events.ToArray();
    }

    private static void AssertOrdered(EngineEvent[] events, params string[] actions)
    {
        var index = 0;
        foreach (var evt in events.OrderBy(e => e.Timestamp))
        {
            if (index < actions.Length && evt.IsAction(actions[index]))
            {
                index++;
            }
        }

        Assert.Equal(actions.Length, index);
    }

    /// <summary>
    /// Tracks disposable engine resources created by runtime tests and removes them during cleanup.
    /// </summary>
    private sealed class WslcRuntimeLease : IAsyncDisposable
    {
        public const string Prefix = "wslcd-rt-";
        public const string WslcPath = "C:\\Program Files\\WSL\\wslc.exe";
        public const string Image = "nginx:alpine";
        private readonly HashSet<string> _containers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _networks = new(StringComparer.Ordinal);
        private readonly HashSet<string> _volumes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _images = new(StringComparer.Ordinal);
        private readonly List<string> _directories = [];
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(5));
        private bool _initialized;

        public WslcRuntimeLease()
        {
            Suffix = Guid.NewGuid().ToString("N")[..12];
            Settings = new RuntimeSettings(WslcPath);
            Runner = new ProcessRunner(Settings);
            Capabilities = new WslcCapabilitiesService(Settings, NullLogger<WslcCapabilitiesService>.Instance);
            var fileTransferStaging = CreateDirectory("file-transfer-staging");
            Service = new WslcService(Runner, NullLogger<WslcService>.Instance, Capabilities, Settings,
                new RestartSuppressionState(), fileTransferStagingRoot: () => fileTransferStaging);
        }

        public string Suffix { get; }
        public RuntimeSettings Settings { get; }
        public ProcessRunner Runner { get; }
        public WslcCapabilitiesService Capabilities { get; }
        public IWslcService Service { get; }
        public CancellationToken Ct => _timeout.Token;

        public async Task InitializeAsync()
        {
            if (!File.Exists(WslcPath))
            {
                throw new InvalidOperationException("wslc.exe was not found at " + WslcPath);
            }

            var version = await Service.GetVersionAsync(Ct);
            Require(version, "wslc version");
            Assert.Contains("3.0.1", version.StandardOutput + version.StandardError, StringComparison.Ordinal);
            var images = await Service.ListImagesAsync(Ct, showAll: true);
            Assert.Contains(images, i => i.Repository == "nginx" && i.Tag == "alpine");
            _initialized = true;
        }

        public string Name(string purpose) => Prefix + Suffix + "-" + purpose;
        public string Repository(string reference) => reference.Split(':')[0];

        public RunContainerOptions Container(string name, string command) => new()
        {
            Image = Image,
            Name = name,
            Command = command,
            Detached = true,
            NeverPull = true,
            Labels = { ["wslcd-rt-owner"] = Suffix },
        };

        public void TrackContainer(string name) => Track(name, _containers);
        public void UntrackContainer(string name) => _containers.Remove(name);
        public void TrackNetwork(string name) => Track(name, _networks);
        public void TrackVolume(string name) => Track(name, _volumes);
        public void TrackImage(string reference)
        {
            if (!reference.StartsWith(Prefix + Suffix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to track unowned image " + reference);
            }

            _images.Add(reference);
        }

        private static void Track(string name, HashSet<string> set)
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to track unowned resource " + name);
            }

            set.Add(name);
        }

        public string CreateDirectory(string purpose)
        {
            var root = Path.Combine(Environment.CurrentDirectory, "RuntimeArtifacts", Name(purpose));
            Directory.CreateDirectory(root);
            _directories.Add(root);
            return root;
        }

        public async Task<JsonElement> InspectContainerJsonAsync(string id, bool includeSize = false)
        {
            var args = new List<string> { "inspect", "--type", "container" };
            if (includeSize) args.Add("--size");
            args.Add(id);
            return await InspectJsonAsync(args);
        }

        public Task<JsonElement> InspectNetworkJsonAsync(string name) => InspectJsonAsync(["network", "inspect", name]);

        private async Task<JsonElement> InspectJsonAsync(IEnumerable<string> args)
        {
            var result = await RawAsync(args);
            Require(result, "inspect");
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                Assert.True(root.GetArrayLength() > 0);
                return root[0].Clone();
            }

            return root.Clone();
        }

        public async Task<JsonElement> WaitForContainerStateAsync(string name, params string[] states)
        {
            JsonElement last = default;
            await SpinUntilAsync(async () =>
            {
                last = await InspectContainerJsonAsync(name);
                return states.Contains(StateStatus(last), StringComparer.OrdinalIgnoreCase);
            }, Ct);
            return last;
        }

        public async Task<CommandResult> RawAsync(IEnumerable<string> args, int seconds = 120)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            return await ProcessRunner.RunAtPathAsync(WslcPath, args, timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            // Cleanup gets its own budget: the test token may already be cancelled (timeout), which is
            // exactly when removing the owned resources matters most.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var cleanupCt = cleanup.Token;
            var errors = new List<Exception>();
            async Task Try(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { errors.Add(ex); }
            }

            await Try(async () =>
            {
                foreach (var name in (await Service.ListContainersAsync(all: true, cleanupCt)).Select(c => c.Name)
                    .Where(n => n.StartsWith(Prefix + Suffix, StringComparison.Ordinal)).Concat(_containers).Distinct(StringComparer.Ordinal).ToArray())
                {
                    await Service.RemoveContainerAsync(name, force: true, cleanupCt, removeAnonymousVolumes: true);
                }
            });
            foreach (var network in _networks.ToArray())
            {
                await Try(async () => await Service.RemoveNetworkAsync(network, cleanupCt));
            }
            foreach (var volume in _volumes.ToArray())
            {
                await Try(async () => await Service.RemoveVolumeAsync(volume, cleanupCt));
            }
            foreach (var image in _images.ToArray())
            {
                await Try(async () => await Service.RemoveImageAsync(image, force: true, cleanupCt));
            }
            foreach (var directory in _directories.AsEnumerable().Reverse())
            {
                await Try(() =>
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                    return Task.CompletedTask;
                });
            }

            if (_initialized)
            {
                await Try(async () =>
                {
                    var leftovers = await FindOwnedLeftoversAsync(cleanupCt);
                    if (leftovers.Count > 0)
                    {
                        throw new InvalidOperationException("Owned runtime resources remain: " + string.Join(", ", leftovers));
                    }
                });
            }

            Capabilities.Dispose();
            Settings.Dispose();
            _timeout.Dispose();
            if (errors.Count > 0)
            {
                throw new AggregateException("Runtime cleanup failed.", errors);
            }
        }

        private async Task<List<string>> FindOwnedLeftoversAsync(CancellationToken cleanupCt)
        {
            var leftovers = new List<string>();
            leftovers.AddRange((await Service.ListContainersAsync(all: true, cleanupCt)).Where(c => c.Name.StartsWith(Prefix + Suffix, StringComparison.Ordinal)).Select(c => "container:" + c.Name));
            leftovers.AddRange((await Service.ListNetworksAsync(cleanupCt)).Where(n => n.Name.StartsWith(Prefix + Suffix, StringComparison.Ordinal)).Select(n => "network:" + n.Name));
            leftovers.AddRange((await Service.ListVolumesAsync(cleanupCt)).Where(v => v.Name.StartsWith(Prefix + Suffix, StringComparison.Ordinal)).Select(v => "volume:" + v.Name));
            leftovers.AddRange((await Service.ListImagesAsync(cleanupCt, showAll: true)).Where(i => i.Repository.StartsWith(Prefix + Suffix, StringComparison.Ordinal)).Select(i => "image:" + i.Reference));
            return leftovers;
        }

        public static void Require(CommandResult result, string context)
        {
            if (!result.Success)
            {
                throw new InvalidOperationException(context + ": " + result.ErrorText);
            }
        }
    }

    /// <summary>
    /// Supplies the runtime test's chosen <c>wslc.exe</c> path through the normal settings interface.
    /// </summary>
    private sealed class RuntimeSettings(string wslcPath) : ISettingsService, IDisposable
    {
        public string WslcPath { get; set; } = wslcPath;
        public int RefreshIntervalSeconds { get; set; }
        public bool CloseToTray { get; set; }
        public bool StartMinimized { get; set; }
        public bool RestartRunningContainersOnLaunch { get; set; }
        public string Theme { get; set; } = "Default";
        public bool NotificationsEnabled { get; set; }
        public bool NotifyImageEvents { get; set; }
        public bool NotifyContainerEvents { get; set; }
        public bool NotifyEngineEvents { get; set; }
        public bool CheckForUpdatesOnLaunch { get; set; }
        public bool AiFeaturesEnabled { get; set; }
        public AiProviderKind AiProvider { get; set; }
        public string AiOllamaEndpoint { get; set; } = string.Empty;
        public string AiOllamaModel { get; set; } = string.Empty;
        public string AiAzureOpenAiEndpoint { get; set; } = string.Empty;
        public string AiAzureOpenAiDeployment { get; set; } = string.Empty;
        public string AiOpenAiEndpoint { get; set; } = string.Empty;
        public string AiOpenAiModel { get; set; } = string.Empty;
        public string AiFoundryLocalEndpoint { get; set; } = string.Empty;
        public string AiFoundryLocalModel { get; set; } = string.Empty;
        public string AiGitHubCopilotModel { get; set; } = string.Empty;
        public bool AiAssistantAutoCreateRun { get; set; }
        public bool AiAssistantAutoLifecycle { get; set; }
        public bool AiAssistantAutoComposeTemplate { get; set; }
        public bool AiAssistantAutoKubernetes { get; set; }
        public IReadOnlyCollection<string> AiAssistantAutoApprovedTools => [];
        public bool AiAssistantApproveEverything { get; set; }
        public bool AiAssistantAllowDestructive { get; set; }
        public string? WslDistro { get; set; }
        public bool WslUpdatePreRelease { get; set; }
        public string? DevContainerNpmRegistry { get; set; }
        public string? K3sInstallerSha256 { get; set; }
        public List<RegistryEntry> Registries { get; set; } = [];
        public List<HealthCheckConfig> HealthChecks { get; set; } = [];
        public List<RestartPolicyConfig> RestartPolicies { get; set; } = [];
        public event EventHandler? Changed;
        public void Load() { }
        public void Save() => Changed?.Invoke(this, EventArgs.Empty);
        public bool IsAssistantToolAutoApproved(string toolName) => false;
        public void SetAssistantToolAutoApproved(string toolName, bool autoApprove) { }
        public void Dispose() => Changed = null;
    }

    /// <summary>
    /// Provides a fixed WSL requirement result so these tests isolate command behavior from machine setup checks.
    /// </summary>
    private sealed class StaticRequirementService(WslRequirementStatus status) : IWslRequirementService
    {
        public WslRequirementStatus Current { get; private set; } = status;
        public bool HasCompletedInitialCheck => true;
        public event EventHandler<WslRequirementStatus>? Changed;
        public Task<WslRequirementStatus> RecheckAsync(CancellationToken ct = default)
        {
            Changed?.Invoke(this, Current);
            return Task.FromResult(Current);
        }
    }

    /// <summary>
    /// Removes registry policy restrictions from runtime scenarios that are testing engine behavior instead.
    /// </summary>
    private sealed class AllowAllPolicy : IWslPolicyService
    {
        public static readonly AllowAllPolicy Instance = new();
        public WslPolicySnapshot GetPolicy() => new(true, true, new WslRegistryAllowlist(WslRegistryAllowlistState.Unrestricted, []));
    }
}
