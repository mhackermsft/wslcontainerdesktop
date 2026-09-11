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

using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;
using Xunit.Sdk;

namespace WslContainerDesktop.Tests.Services;

public sealed class LocalAiSetupServiceTests
{
    private const string Name = "wslcd-ollama";
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ReplacementId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ImageId = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string Operation = "11111111111111111111111111111111";
    private const string OtherOperation = "22222222222222222222222222222222";
    private const string Owner = LocalAiSetupService.OwnerLabel;
    private const string Op = LocalAiSetupService.OperationLabel;
    private const string VolumeOwner = LocalAiSetupService.VolumeLabel;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedRuntimeIsAdoptedWithoutCreatingOrProbing(bool running)
    {
        var h = new Harness { Container = Runtime(running: running), Volume = Models() };
        h.Gpu = WslcCapabilitySupport.Unknown;

        var result = await h.Service.EnsureOllamaContainerAsync(null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(running ? LocalAiContainerState.AlreadyRunning : LocalAiContainerState.StartedExisting, result.State);
        Assert.Equal(Id, result.ContainerId);
        Assert.Equal(LocalRuntimeResourceState.Retained, result.ModelData);
        Assert.Equal(running ? 0 : 1, h.Count(nameof(IWslcService.StartContainerAsync)));
        Assert.Empty(h.Created);
        Assert.Equal(0, h.CapabilityCalls);
        Assert.Equal(0, h.Count(nameof(IWslcService.ListImagesAsync)));
        Assert.Empty(h.Deleted);
        h.AssertSafe();
    }

    /// <summary>
    /// Containers created before per-operation identity labels carry only the owner label. They are
    /// still this app's runtime, so setup adopts them once the port and model mount verify; refusing
    /// would leave the user with a runtime they can neither start nor remove from the app.
    /// </summary>
    [Fact]
    public async Task LegacyOwnedRuntimeWithoutOperationLabelIsAdopted()
    {
        var h = new Harness { Container = Runtime(running: true), Volume = Models(), InventoryId = Id };
        h.Container!["Config"]!["Labels"]!.AsObject().Remove(Op);

        var result = await h.Service.EnsureOllamaContainerAsync(null);

        Assert.True(result.Success);
        Assert.Equal(LocalAiContainerState.AlreadyRunning, result.State);
        Assert.NotNull(h.Container);
        Assert.Empty(h.Created);
        h.AssertNoVolumeDeletion();
        h.AssertSafe();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("malformed-operation")]
    [InlineData("labels")]
    [InlineData("id")]
    [InlineData("short-id")]
    [InlineData("changed-id")]
    [InlineData("mounts")]
    [InlineData("mount-source")]
    [InlineData("mount-destination")]
    [InlineData("mount-name")]
    [InlineData("extra-mount")]
    [InlineData("volume-link")]
    [InlineData("ports")]
    [InlineData("empty-ports")]
    [InlineData("wrong-host-port")]
    [InlineData("wrong-container-port")]
    [InlineData("udp-port")]
    [InlineData("extra-port")]
    [InlineData("public-port")]
    [InlineData("remote-port")]
    [InlineData("unknown-state")]
    [InlineData("contradictory-state")]
    public async Task UnverifiedExistingMetadataNeverStartsOrDeletes(string damage)
    {
        var h = new Harness { Container = Runtime(), Volume = Models(), InventoryId = Id };
        var c = h.Container;
        var labels = c["Config"]!["Labels"]!.AsObject();
        switch (damage)
        {
            case "owner": labels[Owner] = "someone-else"; break;
            case "operation": labels.Remove(Op); break;
            case "malformed-operation": labels[Op] = "not-an-operation"; break;
            case "labels": c["Config"]!.AsObject().Remove("Labels"); break;
            case "id": c.Remove("Id"); break;
            case "short-id": c["Id"] = "aaaaaaaaaaaa"; break;
            case "changed-id": c["Id"] = ReplacementId; break;
            case "mounts": c.Remove("Mounts"); break;
            case "mount-source": c["Mounts"]![0]!["Source"] = "/replacement"; break;
            case "mount-destination": c["Mounts"]![0]!["Destination"] = "/elsewhere"; break;
            case "mount-name": c["Mounts"]![0]!["Name"] = "someone-elses-models"; break;
            case "extra-mount": c["Mounts"]!.AsArray().Add(c["Mounts"]![0]!.DeepClone()); break;
            case "volume-link": labels[VolumeOwner] = OtherOperation; break;
            case "ports": c.Remove("Ports"); break;
            case "empty-ports": c["Ports"] = new JsonArray(); break;
            case "wrong-host-port": c["Ports"]![0]!["HostPort"] = 11435; break;
            case "wrong-container-port": c["Ports"]![0]!["ContainerPort"] = 11435; break;
            case "udp-port": c["Ports"]![0]!["Protocol"] = 17; break;
            case "extra-port": c["Ports"]!.AsArray().Add(c["Ports"]![0]!.DeepClone()); break;
            case "public-port": c["Ports"]![0]!["BindingAddress"] = "0.0.0.0"; break;
            case "remote-port": c["Ports"]![0]!["BindingAddress"] = "192.0.2.10"; break;
            case "unknown-state": c["State"] = new JsonObject { ["Status"] = "unknown" }; break;
            case "contradictory-state": c["State"] = new JsonObject { ["Status"] = "exited", ["Running"] = true }; break;
        }
        // Return the corrupt inspect even when its ID differs from the requested inventory ID.
        h.Overrides[nameof(IWslcService.InspectContainerAsync)] = _ => Task.FromResult(Ok(c.ToJsonString()));

        var result = await h.Service.EnsureOllamaContainerAsync(null);

        Assert.False(result.Success);
        h.AssertNoMutations();
        Assert.NotNull(h.Container);
        Assert.NotNull(h.Volume);
    }

    /// <summary>
    /// A model volume created before ownership labels carries none at all. The owning container's
    /// verified mount is what ties it to this app, so setup reuses it instead of stranding the data.
    /// </summary>
    [Fact]
    public async Task LegacyModelVolumeWithoutLabelsIsReused()
    {
        var h = new Harness { Volume = Models() };
        h.Volume!.Remove("Labels");

        var result = await h.Service.EnsureOllamaContainerAsync(null);

        Assert.True(result.Success);
        Assert.Equal(0, h.Count(nameof(IWslcService.CreateVolumeAsync)));
        Assert.NotNull(h.Volume);
        h.AssertNoVolumeDeletion();
        h.AssertSafe();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("operation")]
    [InlineData("created")]
    [InlineData("mountpoint")]
    public async Task UnownedOrUnverifiableSameNameVolumeIsNeverAdopted(string damage)
    {
        var h = new Harness { Volume = Models() };
        switch (damage)
        {
            case "owner": h.Volume["Labels"]![Owner] = "other"; break;
            case "operation": h.Volume["Labels"]![Op] = "invalid"; break;
            case "created": h.Volume["CreatedAt"] = "unknown"; break;
            case "mountpoint": h.Volume.Remove("Mountpoint"); break;
        }

        Assert.False((await h.Service.EnsureOllamaContainerAsync(null)).Success);
        h.AssertNoMutations();
        Assert.Equal(0, h.CapabilityCalls);
    }

    [Theory]
    [InlineData(false, "{}")]
    [InlineData(false, "[]")]
    [InlineData(false, "[{},{}]")]
    [InlineData(false, "{")]
    [InlineData(false, """{"Id":"x","id":"y"}""")]
    [InlineData(true, "{}")]
    [InlineData(true, "[]")]
    [InlineData(true, "[{},{}]")]
    [InlineData(true, "null")]
    [InlineData(true, """{"Labels":{"owner":"x","Owner":"y"}}""")]
    public async Task LegacyMalformedAndAmbiguousInspectIsRejected(bool volume, string json)
    {
        var h = new Harness { Container = Runtime(), Volume = Models() };
        h.Overrides[volume ? nameof(IWslcService.InspectVolumeAsync) : nameof(IWslcService.InspectContainerAsync)] =
            _ => Task.FromResult(Ok(json));

        Assert.False((await h.Service.EnsureOllamaContainerAsync(null)).Success);
        h.AssertNoMutations();
    }

    [Fact]
    public async Task SingleRootArrayInspectIsAccepted()
    {
        var h = new Harness { Container = Runtime(running: true), Volume = Models() };
        h.Overrides[nameof(IWslcService.InspectContainerAsync)] =
            _ => Task.FromResult(Ok($"[{h.Container.ToJsonString()}]"));
        h.Overrides[nameof(IWslcService.InspectVolumeAsync)] =
            _ => Task.FromResult(Ok($"[{h.Volume.ToJsonString()}]"));
        Assert.True((await h.Service.EnsureOllamaContainerAsync(null)).Success);
        h.AssertNoMutations();
    }

    [Fact]
    public async Task VerifiedDockerPortBindingShapeIsAlsoAccepted()
    {
        var h = new Harness { Container = Runtime(running: true), Volume = Models() };
        h.Container["Ports"] = new JsonObject
        {
            ["11434/tcp"] = new JsonArray(new JsonObject { ["HostIp"] = "127.0.0.1", ["HostPort"] = "11434" }),
        };
        Assert.True((await h.Service.EnsureOllamaContainerAsync(null)).Success);
        h.AssertNoMutations();
    }

    [Theory]
    [InlineData(WslcCapabilitySupport.Supported, false)]
    [InlineData(WslcCapabilitySupport.Supported, true)]
    [InlineData(WslcCapabilitySupport.Unsupported, false)]
    [InlineData(WslcCapabilitySupport.Unsupported, true)]
    public async Task CreationUsesOneCachedOnlyGpuOrPreflightCpuRequest(WslcCapabilitySupport gpu, bool existingVolume)
    {
        var h = new Harness { Gpu = gpu, Volume = existingVolume ? Models() : null };

        var result = await h.Service.EnsureOllamaContainerAsync(null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(gpu == WslcCapabilitySupport.Supported
            ? LocalAiContainerState.CreatedWithGpu : LocalAiContainerState.CreatedCpuOnly, result.State);
        var options = Assert.Single(h.Created);
        Assert.Equal(ImageId, options.Image);
        Assert.Equal(Name, options.Name);
        Assert.True(options.NeverPull);
        Assert.True(options.Detached);
        Assert.Equal(gpu == WslcCapabilitySupport.Supported, options.AllGpus);
        Assert.Equal("127.0.0.1:11434:11434", Assert.Single(options.PortMappings));
        Assert.Equal("wslcd-ollama:/root/.ollama", Assert.Single(options.Volumes));
        Assert.Equal("local-ai", options.Labels[Owner]);
        Assert.True(Guid.TryParseExact(options.Labels[Op], "N", out _));
        Assert.Equal(h.Volume!["Labels"]![Op]!.GetValue<string>(), options.Labels[VolumeOwner]);
        Assert.Equal(1, h.Count(nameof(IWslcService.StartContainerAsync)));
        Assert.Equal(existingVolume ? 0 : 1, h.Count(nameof(IWslcService.CreateVolumeAsync)));
        Assert.True(h.Events.IndexOf("capabilities") < h.Events.IndexOf(nameof(IWslcService.CreateContainerAsync)));
        Assert.Equal(LocalRuntimeResourceState.Retained, result.ModelData);
        h.AssertSafe();
    }

    [Theory]
    [InlineData(WslcCapabilitySupport.Unknown, WslcCapabilitySupport.Supported)]
    [InlineData(WslcCapabilitySupport.Supported, WslcCapabilitySupport.Unknown)]
    [InlineData(WslcCapabilitySupport.Supported, WslcCapabilitySupport.Unsupported)]
    [InlineData(WslcCapabilitySupport.Unsupported, WslcCapabilitySupport.Unknown)]
    [InlineData(WslcCapabilitySupport.Unsupported, WslcCapabilitySupport.Unsupported)]
    public async Task UnknownGpuOrUnavailableCachedOnlySupportAllowsNoMutation(
        WslcCapabilitySupport gpu, WslcCapabilitySupport pull)
    {
        var h = new Harness { Gpu = gpu, Pull = pull };
        Assert.False((await h.Service.EnsureOllamaContainerAsync(null)).Success);
        h.AssertNoMutations();
        Assert.Equal(0, h.Count(nameof(IWslcService.ListImagesAsync)));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("tag-only")]
    [InlineData("other-repository")]
    [InlineData("other-tag")]
    public async Task MissingAuditedCachedImageNeverDownloadsOrMutates(string image)
    {
        var h = new Harness();
        h.Images = image switch
        {
            "absent" => [],
            "tag-only" => [new() { Id = "latest", Repository = "ollama/ollama", Tag = "latest" }],
            "other-repository" => [new() { Id = ImageId, Repository = "unrelated/image", Tag = "latest" }],
            _ => [new() { Id = ImageId, Repository = "ollama/ollama", Tag = "old" }],
        };
        var result = await h.Service.EnsureOllamaContainerAsync(null);
        Assert.False(result.Success);
        Assert.Contains("not on this machine", result.Message);
        Assert.Contains("does not download images", result.Message);
        h.AssertNoMutations();
    }

    [Theory]
    [InlineData(false, "engine unavailable")]
    [InlineData(false, "invalid configuration")]
    [InlineData(false, "GPU unavailable")]
    [InlineData(true, "engine unavailable")]
    [InlineData(true, "invalid configuration")]
    [InlineData(true, "GPU unavailable")]
    public async Task FailedGpuCreateOrStartNeverRetriesCpuAndCleansOnlyCurrentOperation(bool failStart, string error)
    {
        var h = new Harness { CreateFailure = failStart ? null : error, StartFailure = failStart ? error : null };

        var result = await h.Service.EnsureOllamaContainerAsync(null);

        Assert.False(result.Success);
        Assert.True(Assert.Single(h.Created).AllGpus);
        Assert.Equal(failStart ? 1 : 0, h.Count(nameof(IWslcService.StartContainerAsync)));
        Assert.Equal(Id, Assert.Single(h.Deleted));
        Assert.Null(h.Container);
        Assert.NotNull(h.Volume);
        Assert.Equal(LocalRuntimeResourceState.Removed, result.Runtime);
        Assert.Equal(LocalRuntimeResourceState.Retained, result.ModelData);
        h.AssertSafe();
    }

    [Fact]
    public async Task FailedExistingRuntimeStartRetainsRuntimeAndModelsWithoutCleanup()
    {
        var h = new Harness { Container = Runtime(), Volume = Models(), StartFailure = "engine error" };
        var result = await h.Service.EnsureOllamaContainerAsync(null);
        Assert.False(result.Success);
        Assert.Empty(h.Created);
        Assert.Empty(h.Deleted);
        Assert.Equal(LocalRuntimeResourceState.Retained, result.Runtime);
        Assert.NotNull(h.Container);
        h.AssertSafe();
    }

    [Theory]
    [InlineData(false, "io")]
    [InlineData(false, "process")]
    [InlineData(false, "timeout")]
    [InlineData(false, "configuration")]
    [InlineData(true, "io")]
    [InlineData(true, "process")]
    [InlineData(true, "timeout")]
    [InlineData(true, "configuration")]
    public async Task ThrownCreateOrStartFailuresAreNotCpuFallbackEvidence(bool start, string failure)
    {
        var h = new Harness();
        h.Overrides[start ? nameof(IWslcService.StartContainerAsync) : nameof(IWslcService.CreateContainerAsync)] = args =>
        {
            if (!start) h.Materialize((RunContainerOptions)args[0]!);
            Exception error = failure switch
            {
                "io" => new IOException("synthetic engine unavailable"),
                "process" => new System.ComponentModel.Win32Exception("synthetic process failed"),
                "timeout" => new TimeoutException("synthetic timeout"),
                _ => new InvalidOperationException("synthetic invalid configuration"),
            };
            return Task.FromException<CommandResult>(error);
        };
        var result = await h.Service.EnsureOllamaContainerAsync(null);
        Assert.False(result.Success);
        Assert.True(Assert.Single(h.Created).AllGpus);
        Assert.Equal(start ? 1 : 0, h.Count(nameof(IWslcService.StartContainerAsync)));
        Assert.Equal(Id, Assert.Single(h.Deleted));
        Assert.Equal(LocalRuntimeResourceState.Removed, result.Runtime);
        h.AssertSafe();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContainerOrVolumeAppearingDuringPreparationPreventsCreation(bool replaceVolume)
    {
        var h = new Harness { Volume = Models() };
        h.Before = (method, _) =>
        {
            if (method == nameof(IWslcService.ListContainersAsync) && h.Count(method) == 2)
            {
                if (replaceVolume) h.Volume = Models(OtherOperation);
                else h.Container = Runtime(ReplacementId, OtherOperation);
            }
        };
        Assert.False((await h.Service.EnsureOllamaContainerAsync(null)).Success);
        h.AssertNoMutations();
        Assert.Equal(0, h.Count(nameof(IWslcService.StartContainerAsync)));
        Assert.NotNull(h.Volume);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateInventoryNamesAreConflictsNotEmptyInventory(bool volumes)
    {
        var h = new Harness();
        if (volumes)
            h.Overrides[nameof(IWslcService.ListVolumesAsync)] = _ =>
                Task.FromResult<IReadOnlyList<VolumeInfo>>([new() { Name = Name }, new() { Name = Name.ToUpperInvariant() }]);
        else
            h.Overrides[nameof(IWslcService.ListContainersAsync)] = _ =>
                Task.FromResult<IReadOnlyList<ContainerInfo>>(
                    [new() { Id = Id, Name = Name }, new() { Id = ReplacementId, Name = Name.ToUpperInvariant() }]);
        Assert.False((await h.Service.EnsureOllamaContainerAsync(null)).Success);
        h.AssertNoMutations();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedVolumeInventoryIsUnknownNotAbsentAndNeverMutates(bool existingRuntime, bool remove)
    {
        var h = new Harness { Container = existingRuntime ? Runtime() : null, Volume = Models() };
        h.Overrides[nameof(IWslcService.ListVolumesAsync)] = _ =>
            Task.FromException<IReadOnlyList<VolumeInfo>>(new InvalidOperationException("synthetic volume inventory failure"));
        if (remove)
        {
            var result = await h.Service.RemoveOllamaContainerAsync(true);
            Assert.False(result.Success);
            Assert.Equal(LocalRuntimeResourceState.Unknown, result.ModelData);
        }
        else
        {
            var result = await h.Service.EnsureOllamaContainerAsync(null);
            Assert.False(result.Success);
            Assert.Equal(LocalRuntimeResourceState.Unknown, result.ModelData);
            Assert.Equal(existingRuntime ? LocalRuntimeResourceState.Retained : LocalRuntimeResourceState.Absent, result.Runtime);
        }
        Assert.NotNull(h.Volume);
        h.AssertNoMutations();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedImageInventoryRetainsKnownResourceStatesAndNeverDownloads(bool existingModels)
    {
        var h = new Harness { Volume = existingModels ? Models() : null };
        h.Overrides[nameof(IWslcService.ListImagesAsync)] = _ =>
            Task.FromException<IReadOnlyList<ImageInfo>>(new InvalidOperationException("synthetic image inventory failure"));
        var result = await h.Service.EnsureOllamaContainerAsync(null);
        Assert.False(result.Success);
        Assert.Equal(LocalRuntimeResourceState.Absent, result.Runtime);
        Assert.Equal(existingModels ? LocalRuntimeResourceState.Retained : LocalRuntimeResourceState.Absent, result.ModelData);
        Assert.DoesNotContain("not on this machine", result.Message);
        h.AssertNoMutations();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrUnattributedVolumeCreationNeverCreatesRuntimeOrDeletesData(bool unattributed)
    {
        var h = new Harness();
        h.Overrides[nameof(IWslcService.CreateVolumeAsync)] = _ =>
        {
            h.Volume = Models(OtherOperation);
            return Task.FromResult(unattributed ? Ok(Name) : Fail("partially completed volume creation"));
        };
        var result = await h.Service.EnsureOllamaContainerAsync(null);
        Assert.False(result.Success);
        Assert.Equal(LocalRuntimeResourceState.Unknown, result.ModelData);
        Assert.Empty(h.Created);
        Assert.Empty(h.Deleted);
        Assert.Equal(1, h.Count(nameof(IWslcService.CreateVolumeAsync)));
        Assert.Equal(0, h.Count(nameof(IWslcService.StartContainerAsync)));
        Assert.NotNull(h.Volume);
        h.AssertSafe();
    }

    [Theory]
    [InlineData("before-find")]
    [InlineData("before-inspect")]
    [InlineData("before-delete")]
    public async Task CleanupRetainsReplacementAcrossNameAndImmutableIdRaces(string race)
    {
        var h = new Harness { CreateFailure = "partial failure", ReturnCreatedId = race != "before-find" };
        var swapped = false;
        h.Before = (method, _) =>
        {
            var trigger = race switch
            {
                "before-find" => nameof(IWslcService.ListContainersAsync),
                "before-inspect" => nameof(IWslcService.InspectContainerAsync),
                _ => nameof(IWslcService.RemoveContainerAsync),
            };
            if (!swapped && h.Created.Count == 1 && method == trigger)
            {
                h.Container = Runtime(ReplacementId, OtherOperation);
                swapped = true;
            }
        };

        var result = await h.Service.EnsureOllamaContainerAsync(null);

        Assert.False(result.Success);
        Assert.True(swapped);
        Assert.Equal(ReplacementId, h.Container!["Id"]!.GetValue<string>());
        Assert.DoesNotContain(ReplacementId, h.Deleted);
        Assert.Equal(race == "before-delete" ? 1 : 0, h.Deleted.Count);
        Assert.Single(h.Created);
        h.AssertSafe();
    }

    [Fact]
    public async Task CreationReturningDifferentOperationIsNeverStartedOrRemoved()
    {
        var h = new Harness();
        h.Before = (method, _) =>
        {
            if (method == nameof(IWslcService.InspectContainerAsync))
                h.Container!["Config"]!["Labels"]![Op] = OtherOperation;
        };
        var result = await h.Service.EnsureOllamaContainerAsync(null);
        Assert.False(result.Success);
        Assert.Equal(0, h.Count(nameof(IWslcService.StartContainerAsync)));
        Assert.Empty(h.Deleted);
        Assert.NotNull(h.Container);
        h.AssertSafe();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureOrUnconfirmedDeletionReportsUnknown(bool unchanged)
    {
        var h = new Harness { StartFailure = "start failed", RemoveFailure = unchanged ? null : "delete failed", KeepRemoved = unchanged };
        var result = await h.Service.EnsureOllamaContainerAsync(null);
        Assert.False(result.Success);
        Assert.Equal(LocalRuntimeResourceState.Unknown, result.Runtime);
        Assert.Contains("cleanup could not be confirmed", result.Message);
        Assert.NotNull(h.Container);
        Assert.NotNull(h.Volume);
        Assert.Single(h.Created);
        Assert.Single(h.Deleted);
        h.AssertSafe();
    }

    [Fact]
    public async Task CancelledCreateFindsPartialRuntimeWithFreshTokenAndDoesNotRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new Harness();
        h.Overrides[nameof(IWslcService.CreateContainerAsync)] = args =>
        {
            var options = (RunContainerOptions)args[0]!;
            h.Materialize(options);
            cancellation.Cancel();
            return Task.FromCanceled<CommandResult>(cancellation.Token);
        };

        var result = await h.Service.EnsureOllamaContainerAsync(null, cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(LocalAiContainerState.Cancelled, result.State);
        Assert.Equal(LocalRuntimeResourceState.Removed, result.Runtime);
        Assert.Single(h.Created);
        Assert.Equal(Id, Assert.Single(h.Deleted));
        Assert.Equal(0, h.Count(nameof(IWslcService.StartContainerAsync)));
        Assert.NotNull(h.Volume);
        Assert.All(h.RemovalTokens, token => Assert.False(token.IsCancellationRequested));
        h.AssertSafe();
    }

    [Fact]
    public async Task CancelledCreateWithoutObservablePartialReportsRecoveryNotAbsence()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new Harness();
        h.Overrides[nameof(IWslcService.CreateContainerAsync)] = _ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<CommandResult>(cancellation.Token);
        };
        var result = await h.Service.EnsureOllamaContainerAsync(null, cancellation.Token);
        Assert.Equal(LocalAiContainerState.Cancelled, result.State);
        Assert.Equal(LocalRuntimeResourceState.Unknown, result.Runtime);
        Assert.Contains("may still finish", result.Message);
        Assert.Single(h.Created);
        Assert.Empty(h.Deleted);
        h.AssertSafe();
    }

    [Fact]
    public async Task SetupAndRemovalAreSerializedUntilFirstOperationFinishes()
    {
        var entered = AiContractHarness.Signal<bool>();
        var release = AiContractHarness.Signal<CommandResult>();
        var h = new Harness();
        h.Overrides[nameof(IWslcService.CreateContainerAsync)] = args =>
        {
            h.Materialize((RunContainerOptions)args[0]!);
            entered.TrySetResult(true);
            return release.Task;
        };
        var setup = h.Service.EnsureOllamaContainerAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var count = h.Events.Count;
        var secondSetup = h.Service.EnsureOllamaContainerAsync(null);
        var removal = h.Service.RemoveOllamaContainerAsync(false);
        Assert.False(secondSetup.IsCompleted);
        Assert.False(removal.IsCompleted);
        Assert.Equal(count, h.Events.Count);
        release.SetResult(Ok(Id));

        Assert.True((await setup.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.True((await secondSetup.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.True((await removal.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Single(h.Created);
        Assert.Single(h.Deleted);
        h.AssertSafe();
    }

    [Fact]
    public async Task CancelledQueuedSetupDoesNotProbeOrMutateAndDoesNotReleaseAnotherOperationsGate()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = AiContractHarness.Signal<bool>();
        var release = AiContractHarness.Signal<CommandResult>();
        var h = new Harness();
        h.Overrides[nameof(IWslcService.CreateContainerAsync)] = args =>
        {
            h.Materialize((RunContainerOptions)args[0]!);
            entered.TrySetResult(true);
            return release.Task;
        };
        var first = h.Service.EnsureOllamaContainerAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var events = h.Events.Count;
        var cancelled = h.Service.EnsureOllamaContainerAsync(null, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(events, h.Events.Count);
        var subsequent = h.Service.EnsureOllamaContainerAsync(null);
        Assert.False(subsequent.IsCompleted);
        Assert.Equal(events, h.Events.Count);
        release.SetResult(Ok(Id));
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(LocalAiContainerState.AlreadyRunning,
            (await subsequent.WaitAsync(TimeSpan.FromSeconds(5))).State);
        Assert.Single(h.Created);
        h.AssertSafe();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalDeletesRuntimeAndOnlyDeletesModelsWhenRequested(bool deleteModels)
    {
        var h = new Harness { Container = Runtime(running: true), Volume = Models() };
        var result = await h.Service.RemoveOllamaContainerAsync(deleteModels);
        Assert.True(result.Success);
        Assert.Equal(LocalRuntimeResourceState.Removed, result.Runtime);
        Assert.Equal(deleteModels ? LocalRuntimeResourceState.Removed : LocalRuntimeResourceState.Retained,
            result.ModelData);
        Assert.Equal(Id, Assert.Single(h.Deleted));
        Assert.Null(h.Container);
        if (deleteModels)
        {
            Assert.Null(h.Volume);
        }
        else
        {
            Assert.NotNull(h.Volume);
            h.AssertNoVolumeDeletion();
        }
        Assert.Empty(h.Created);
        h.AssertSafe();
    }

    [Fact]
    public async Task RequestedModelDeletionFailureKeepsDataAndReportsIt()
    {
        var h = new Harness
        {
            Container = Runtime(running: true), Volume = Models(), VolumeRemoveFailure = "volume busy",
        };
        var result = await h.Service.RemoveOllamaContainerAsync(true);
        Assert.False(result.Success);
        Assert.Equal(LocalRuntimeResourceState.Removed, result.Runtime);
        Assert.Equal(LocalRuntimeResourceState.Retained, result.ModelData);
        Assert.NotNull(h.Volume);
        Assert.Contains("could not be deleted", result.Message);
        h.AssertSafe();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RemovalWithoutRuntimeStillDeletesRequestedModels(bool hasVolume, bool deleteModels)
    {
        var h = new Harness { Volume = hasVolume ? Models() : null };
        var result = await h.Service.RemoveOllamaContainerAsync(deleteModels);
        Assert.True(result.Success);
        Assert.Equal(LocalRuntimeResourceState.Absent, result.Runtime);
        Assert.Equal(hasVolume && !deleteModels ? LocalRuntimeResourceState.Retained
            : hasVolume ? LocalRuntimeResourceState.Removed : LocalRuntimeResourceState.Absent,
            result.ModelData);
        if (hasVolume && deleteModels)
        {
            Assert.Null(h.Volume);
        }
        h.AssertSafe();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeWithMissingVolumeCannotBeStartedButCanBeRemoved(bool remove)
    {
        var h = new Harness { Container = Runtime() };
        if (remove)
        {
            // Removal must not depend on model-volume metadata; that check guards starting a
            // workload, and requiring it here would block recovery from a broken runtime.
            Assert.True((await h.Service.RemoveOllamaContainerAsync(true)).Success);
            Assert.Null(h.Container);
        }
        else
        {
            Assert.False((await h.Service.EnsureOllamaContainerAsync(null)).Success);
            h.AssertNoMutations();
            Assert.NotNull(h.Container);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalMetadataFailureDoesNotDeleteAnything(bool volume)
    {
        var h = new Harness { Container = Runtime(), Volume = Models() };
        h.Overrides[volume ? nameof(IWslcService.InspectVolumeAsync) : nameof(IWslcService.InspectContainerAsync)] =
            _ => Task.FromResult(Fail("inspect failed"));
        var result = await h.Service.RemoveOllamaContainerAsync(true);
        Assert.False(result.Success);
        h.AssertNoMutations();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrUnconfirmedRemovalReportsUnknownRuntimeAndRetainedModels(bool unconfirmed)
    {
        var h = new Harness
        {
            Container = Runtime(), Volume = Models(), KeepRemoved = unconfirmed,
            RemoveFailure = unconfirmed ? null : "engine removal failed",
        };
        var result = await h.Service.RemoveOllamaContainerAsync(true);
        Assert.False(result.Success);
        Assert.Equal(LocalRuntimeResourceState.Unknown, result.Runtime);
        Assert.Equal(LocalRuntimeResourceState.Retained, result.ModelData);
        Assert.Single(h.Deleted);
        Assert.NotNull(h.Container);
        Assert.NotNull(h.Volume);
        h.AssertSafe();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalRetainsReplacedContainerOrVolume(bool replaceVolume)
    {
        var h = new Harness { Container = Runtime(), Volume = Models() };
        h.Before = (method, _) =>
        {
            if (method == nameof(IWslcService.InspectContainerAsync) && h.Count(method) == 2)
            {
                if (replaceVolume) h.Volume = Models(OtherOperation);
                else h.Container = Runtime(ReplacementId, OtherOperation);
            }
        };
        Assert.False((await h.Service.RemoveOllamaContainerAsync(true)).Success);
        // Replacement data is never deleted. A replaced container aborts before any mutation; a
        // volume swapped after the container is gone keeps its data instead of deleting a stranger's.
        Assert.NotNull(h.Volume);
        h.AssertNoVolumeDeletion();
        if (replaceVolume)
        {
            Assert.Null(h.Container);
            h.AssertSafe();
        }
        else
        {
            Assert.NotNull(h.Container);
            h.AssertNoMutations();
        }
    }

    [Fact]
    public async Task RemovalTargetsOriginalImmutableIdEvenIfNameReplacedAtDelete()
    {
        var h = new Harness { Container = Runtime(), Volume = Models() };
        h.Before = (method, _) =>
        {
            if (method == nameof(IWslcService.RemoveContainerAsync))
                h.Container = Runtime(ReplacementId, OtherOperation);
        };
        await h.Service.RemoveOllamaContainerAsync(false);
        Assert.Equal(Id, Assert.Single(h.Deleted));
        Assert.Equal(ReplacementId, h.Container!["Id"]!.GetValue<string>());
        h.AssertSafe();
    }

    [Fact]
    public async Task VolumeSnapshotChangingBeforeStartPreventsAdoption()
    {
        var h = new Harness { Container = Runtime(), Volume = Models() };
        h.Before = (method, _) =>
        {
            if (method == nameof(IWslcService.InspectVolumeAsync) && h.Count(method) == 2)
                h.Volume!["CreatedAt"] = "2026-02-02T00:00:00Z";
        };
        Assert.False((await h.Service.EnsureOllamaContainerAsync(null)).Success);
        h.AssertNoMutations();
    }

    private static CommandResult Ok(string output = "") => new() { StandardOutput = output };
    private static CommandResult Fail(string error) => new() { ExitCode = 1, StandardError = error };

    private static JsonObject Models(string operation = Operation) => new()
    {
        ["Name"] = Name,
        ["CreatedAt"] = "2026-01-01T00:00:00Z",
        ["Mountpoint"] = "/var/lib/volumes/wslcd-ollama",
        ["Labels"] = new JsonObject { [Owner] = "local-ai", [Op] = operation },
    };

    private static JsonObject Runtime(string id = Id, string operation = Operation,
        string volume = Operation, bool running = false) => new()
    {
        ["Id"] = id,
        ["Name"] = Name,
        ["Config"] = new JsonObject
        {
            ["Labels"] = new JsonObject { [Owner] = "local-ai", [Op] = operation, [VolumeOwner] = volume },
        },
        ["State"] = new JsonObject { ["Running"] = running, ["Status"] = running ? "running" : "exited" },
        ["Ports"] = new JsonArray(new JsonObject
        {
            ["BindingAddress"] = "127.0.0.1", ["HostPort"] = 11434, ["ContainerPort"] = 11434, ["Protocol"] = 6,
        }),
        ["Mounts"] = new JsonArray(new JsonObject
        {
            ["Type"] = "volume", ["Name"] = Name, ["Source"] = "/var/lib/volumes/wslcd-ollama",
            ["Destination"] = "/root/.ollama", ["ReadWrite"] = true,
        }),
    };

    private sealed class Harness
    {
        public JsonObject? Container { get; set; }
        public JsonObject? Volume { get; set; }
        public string? InventoryId { get; set; }
        public WslcCapabilitySupport Gpu { get; set; } = WslcCapabilitySupport.Supported;
        public WslcCapabilitySupport Pull { get; set; } = WslcCapabilitySupport.Supported;
        public IReadOnlyList<ImageInfo> Images { get; set; } =
            [new() { Id = ImageId, Repository = "ollama/ollama", Tag = "latest" }];
        public string? CreateFailure { get; set; }
        public string? StartFailure { get; set; }
        public string? RemoveFailure { get; set; }
        public string? VolumeRemoveFailure { get; set; }
        public bool ReturnCreatedId { get; set; } = true;
        public bool KeepRemoved { get; set; }
        public bool KeepRemovedVolume { get; set; }
        public Action<string, object?[]>? Before { get; set; }
        public Dictionary<string, Func<object?[], object>> Overrides { get; } = [];
        public List<string> Events { get; } = [];
        public List<RunContainerOptions> Created { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<CancellationToken> RemovalTokens { get; } = [];
        public LocalAiSetupService Service { get; }
        public int CapabilityCalls { get; private set; }
        private int _invalidations;
        private int _lastMutationInvalidation;
        private readonly List<string> _unexpected = [];
        private readonly List<string> _mutations = [];

        public Harness()
        {
            var ai = NetworkTestProxy.Create<IAiCapabilityService>((method, _) =>
            {
                if (method.Name != nameof(IAiCapabilityService.Invalidate))
                    throw Unexpected(method.Name);
                _invalidations++;
                Events.Add("invalidate");
                return null;
            });
            var capabilities = NetworkTestProxy.Create<IWslcCapabilitiesService>((method, _) =>
            {
                if (method.Name != nameof(IWslcCapabilitiesService.GetAsync))
                    throw Unexpected(method.Name);
                CapabilityCalls++;
                Events.Add("capabilities");
                return Task.FromResult(new WslcCapabilities("synthetic-wslc-not-executable", "synthetic",
                    new Dictionary<WslcFeature, WslcCapability>
                    {
                        [WslcFeature.CreateGpus] = new(Gpu, "synthetic GPU help evidence"),
                        [WslcFeature.CreatePull] = new(Pull, "synthetic cached-only help evidence"),
                    }));
            });
            Service = new(NetworkTestProxy.Create<IWslcService>(Invoke), capabilities, ai,
                NullLogger<LocalAiSetupService>.Instance);
        }

        public int Count(string method) => Events.Count(e => e == method);

        public void Materialize(RunContainerOptions options) =>
            Container = Runtime(operation: options.Labels[Op], volume: options.Labels[VolumeOwner]);

        private object Invoke(MethodInfo method, object?[] args)
        {
            var name = method.Name;
            Events.Add(name);
            if (name is nameof(IWslcService.CreateVolumeAsync) or nameof(IWslcService.CreateContainerAsync)
                or nameof(IWslcService.StartContainerAsync) or nameof(IWslcService.RemoveContainerAsync))
            {
                Assert.True(_invalidations > _lastMutationInvalidation, $"Capability invalidation must precede {name}.");
                _lastMutationInvalidation = _invalidations;
                _mutations.Add(name);
            }
            if (name == nameof(IWslcService.CreateContainerAsync)) Created.Add((RunContainerOptions)args[0]!);
            if (name == nameof(IWslcService.RemoveContainerAsync))
            {
                Deleted.Add((string)args[0]!);
                Assert.True((bool)args[1]!);
                var token = (CancellationToken)args[2]!;
                Assert.False(token.IsCancellationRequested);
                RemovalTokens.Add(token);
            }
            Before?.Invoke(name, args);
            if (Overrides.TryGetValue(name, out var action)) return action(args);
            switch (name)
            {
                case nameof(IWslcService.ListContainersAsync):
                    Assert.True((bool)args[0]!);
                    return Task.FromResult<IReadOnlyList<ContainerInfo>>(Container is null ? [] :
                        [new() { Id = InventoryId ?? Container["Id"]!.GetValue<string>(), Name = Name }]);
                case nameof(IWslcService.InspectContainerAsync):
                    return Task.FromResult(Container is not null && (string)args[0]! == Container["Id"]?.GetValue<string>()
                        ? Ok(Container.ToJsonString()) : Fail("immutable target absent"));
                case nameof(IWslcService.ListVolumesAsync):
                    return Task.FromResult<IReadOnlyList<VolumeInfo>>(Volume is null ? [] : [new() { Name = Name }]);
                case nameof(IWslcService.InspectVolumeAsync):
                    Assert.Equal(Name, args[0]);
                    return Task.FromResult(Volume is null ? Fail("volume absent") : Ok(Volume.ToJsonString()));
                case nameof(IWslcService.ListImagesAsync):
                    return Task.FromResult(Images);
                case nameof(IWslcService.CreateVolumeAsync):
                    Assert.Equal(Name, args[0]);
                    var labels = (IReadOnlyDictionary<string, string>)args[3]!;
                    Assert.Equal("local-ai", labels[Owner]);
                    Volume = Models(labels[Op]);
                    return Task.FromResult(Ok(Name));
                case nameof(IWslcService.CreateContainerAsync):
                    Materialize((RunContainerOptions)args[0]!);
                    return Task.FromResult(new CommandResult
                    {
                        ExitCode = CreateFailure is null ? 0 : 1,
                        StandardError = CreateFailure ?? "",
                        StandardOutput = ReturnCreatedId ? Id : "",
                    });
                case nameof(IWslcService.StartContainerAsync):
                    Assert.Equal(Container!["Id"]!.GetValue<string>(), args[0]);
                    if (StartFailure is not null) return Task.FromResult(Fail(StartFailure));
                    Container["State"] = new JsonObject { ["Running"] = true, ["Status"] = "running" };
                    return Task.FromResult(Ok());
                case nameof(IWslcService.RemoveContainerAsync):
                    if (RemoveFailure is not null) return Task.FromResult(Fail(RemoveFailure));
                    if (!KeepRemoved && Container?["Id"]?.GetValue<string>() == (string)args[0]!) Container = null;
                    return Task.FromResult(Ok());
                case nameof(IWslcService.RemoveVolumeAsync):
                    Assert.Equal(Name, args[0]);
                    if (VolumeRemoveFailure is not null) return Task.FromResult(Fail(VolumeRemoveFailure));
                    if (!KeepRemovedVolume) Volume = null;
                    return Task.FromResult(Ok());
                default:
                    throw Unexpected(name);
            }
        }

        // Assertion exceptions are intentionally outside the service's lifecycle catch filter.
        // Pull/run/exec/volume deletion, live probes, and every other unconfigured I/O fail the test.
        private XunitException Unexpected(string name)
        {
            _unexpected.Add(name);
            return new XunitException($"Unexpected external operation: {name}; no real process/network fallback exists.");
        }

        /// <summary>Volume deletion is a real, user-approved outcome, so it is asserted per test via
        /// <see cref="AssertNoVolumeDeletion"/> rather than banned outright here.</summary>
        public void AssertSafe()
        {
            Assert.Empty(_unexpected);
            Assert.Equal(0, Count(nameof(IWslcService.PullImageAsync)));
            Assert.Equal(0, Count(nameof(IWslcService.RunContainerAsync)));
            Assert.Equal(0, Count(nameof(IWslcService.ExecAsync)));
            Assert.Equal("invalidate", Events[^1]);
        }

        public void AssertNoVolumeDeletion() =>
            Assert.Equal(0, Count(nameof(IWslcService.RemoveVolumeAsync)));

        public void AssertNoMutations()
        {
            Assert.Empty(_mutations);
            AssertNoVolumeDeletion();
            AssertSafe();
        }
    }
}

