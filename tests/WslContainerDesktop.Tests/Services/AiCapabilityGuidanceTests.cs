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

public sealed class AiCapabilityGuidanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosisUsesSameSafeContextEvenWhenEvidenceIsUnavailable(bool unavailable)
    {
        var capabilities = NetworkTestProxy.Create<IWslcCapabilitiesService>((_, _) => unavailable
            ? Task.FromException<WslcCapabilities>(new IOException("private-probe-path"))
            : Task.FromResult(new WslcCapabilities("private-path", "private-version",
                new Dictionary<WslcFeature, WslcCapability>
                {
                    [WslcFeature.NetworkConnect] = new(WslcCapabilitySupport.Unsupported),
                })));
        var engine = NetworkTestProxy.Create<IWslcService>((_, _) => Task.FromResult(new CommandResult()));
        var activity = NetworkTestProxy.Create<IActivityLog>((_, _) =>
            new System.Collections.ObjectModel.ObservableCollection<ActivityEvent>());
        var service = new AiDiagnosticsService(engine, activity, new AiContractHarness().Settings,
            capabilities, [], Microsoft.Extensions.Logging.Abstractions.NullLogger<AiDiagnosticsService>.Instance);
        var preview = await service.BuildPreviewAsync(new ContainerInfo
            { Name = "fixture", Id = "fixture", StateValue = (int)ContainerState.Stopped });
        Assert.Contains(await AiCapabilityGuidance.GetAsync(capabilities, default), preview.Request.SystemPrompt);
        Assert.DoesNotContain("private-", preview.Request.SystemPrompt);
        Assert.Contains(unavailable ? "unavailable" : "NetworkConnect: Unsupported", preview.Request.SystemPrompt);
    }

    [Fact]
    public async Task CancelledCapabilityLookupNeverReturnsUnavailableFallback()
    {
        var service = NetworkTestProxy.Create<IWslcCapabilitiesService>((_, _) =>
            Task.FromException<WslcCapabilities>(new OperationCanceledException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AiCapabilityGuidance.GetAsync(service, default));
    }

    [Theory]
    [InlineData(WslcCapabilitySupport.Supported)]
    [InlineData(WslcCapabilitySupport.Unsupported)]
    [InlineData(WslcCapabilitySupport.Unknown)]
    public void PreservesEachDetectedStateWithoutLeakingProbeData(WslcCapabilitySupport support)
    {
        var capabilities = new WslcCapabilities("private-executable", "private-version",
            Enum.GetValues<WslcFeature>().ToDictionary(feature => feature,
                _ => new WslcCapability(support, "private-diagnostic")), "private-version-error");

        var guidance = AiCapabilityGuidance.Build(capabilities);

        foreach (var feature in Enum.GetValues<WslcFeature>())
            Assert.Contains($"{feature}: {support}", guidance);
        Assert.DoesNotContain("private-", guidance);
        Assert.Contains("Never treat Unknown as Supported", guidance);
        Assert.Contains("distinct from restart/auto-heal", guidance);
    }

    [Fact]
    public void MissingEvidenceRemainsUnknownAndRunCreateAreIndependent()
    {
        var capabilities = new WslcCapabilities("", null, new Dictionary<WslcFeature, WslcCapability>
        {
            [WslcFeature.HealthCmd] = new(WslcCapabilitySupport.Supported),
            [WslcFeature.CreateHealthCmd] = new(WslcCapabilitySupport.Unsupported),
        });

        var guidance = AiCapabilityGuidance.Build(capabilities);

        Assert.Contains("\nHealthCmd: Supported", guidance);
        Assert.Contains("\nCreateHealthCmd: Unsupported", guidance);
        Assert.Contains("\nContainerCp: Unknown", guidance);
        Assert.Contains("\nNetworkConnect: Unknown", guidance);
    }
}
