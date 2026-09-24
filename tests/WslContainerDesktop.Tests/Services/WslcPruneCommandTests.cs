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

public sealed class WslcPruneCommandTests
{
    public static TheoryData<WslcPruneTarget, WslcFeature, string[]> Targets => new()
    {
        { WslcPruneTarget.Containers, WslcFeature.ContainerPruneForce, ["container", "prune"] },
        { WslcPruneTarget.Images, WslcFeature.ImagePruneForce, ["image", "prune"] },
        // --all is pre-existing behavior (named volumes too) and must survive the --force change.
        { WslcPruneTarget.Volumes, WslcFeature.VolumePruneForce, ["volume", "prune", "--all"] },
        { WslcPruneTarget.Networks, WslcFeature.NetworkPruneForce, ["network", "prune"] },
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public void Supported_AppendsForceSoTheEngineDoesNotPrompt(WslcPruneTarget target, WslcFeature feature, string[] baseline)
    {
        var selection = WslcPruneCommand.Select(target, Snapshot((feature, WslcCapabilitySupport.Supported)));

        Assert.Null(selection.Error);
        Assert.Equal([.. baseline, "--force"], selection.Arguments);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Unsupported_RunsTheUnchangedLegacyCommand(WslcPruneTarget target, WslcFeature feature, string[] baseline)
    {
        // WSLC 2.9.9 rejects --force on prune; it also never prompts, so the plain command is correct.
        var selection = WslcPruneCommand.Select(target, Snapshot((feature, WslcCapabilitySupport.Unsupported)));

        Assert.Null(selection.Error);
        Assert.Equal(baseline, selection.Arguments);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Unknown_RunsNothingAndCarriesTheProbeDiagnostic(WslcPruneTarget target, WslcFeature feature, string[] baseline)
    {
        _ = baseline;
        var snapshot = Snapshot((feature, new WslcCapability(WslcCapabilitySupport.Unknown, "probe said: access denied")));

        var selection = WslcPruneCommand.Select(target, snapshot);

        Assert.Null(selection.Arguments);
        Assert.StartsWith("Nothing was pruned", selection.Error);
        Assert.Contains("probe said: access denied", selection.Error);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void MissingEvidence_IsTreatedAsUnknown(WslcPruneTarget target, WslcFeature feature, string[] baseline)
    {
        _ = (feature, baseline);
        var selection = WslcPruneCommand.Select(target, new WslcCapabilities("wslc.exe", null,
            new Dictionary<WslcFeature, WslcCapability>()));

        Assert.Null(selection.Arguments);
        Assert.NotNull(selection.Error);
    }

    [Fact]
    public void EachTargetUsesOnlyItsOwnFeature()
    {
        // Only containers advertise --force here; no other resource may borrow that evidence.
        var snapshot = Snapshot(
            (WslcFeature.ContainerPruneForce, WslcCapabilitySupport.Supported),
            (WslcFeature.ImagePruneForce, WslcCapabilitySupport.Unsupported),
            (WslcFeature.VolumePruneForce, WslcCapabilitySupport.Unsupported),
            (WslcFeature.NetworkPruneForce, WslcCapabilitySupport.Unknown));

        Assert.Contains("--force", WslcPruneCommand.Select(WslcPruneTarget.Containers, snapshot).Arguments!);
        Assert.DoesNotContain("--force", WslcPruneCommand.Select(WslcPruneTarget.Images, snapshot).Arguments!);
        Assert.DoesNotContain("--force", WslcPruneCommand.Select(WslcPruneTarget.Volumes, snapshot).Arguments!);
        Assert.Null(WslcPruneCommand.Select(WslcPruneTarget.Networks, snapshot).Arguments);
    }

    [Fact]
    public void RepeatedSelection_DoesNotAccumulateArguments()
    {
        var snapshot = Snapshot((WslcFeature.VolumePruneForce, WslcCapabilitySupport.Supported));

        WslcPruneCommand.Select(WslcPruneTarget.Volumes, snapshot);
        var second = WslcPruneCommand.Select(WslcPruneTarget.Volumes, snapshot);

        Assert.Equal(["volume", "prune", "--all", "--force"], second.Arguments);
    }

    private static WslcCapabilities Snapshot(params (WslcFeature Feature, WslcCapabilitySupport Support)[] features) =>
        Snapshot(features.Select(f => (f.Feature, new WslcCapability(f.Support,
            f.Support == WslcCapabilitySupport.Supported ? null : $"{f.Feature} {f.Support}"))).ToArray());

    private static WslcCapabilities Snapshot(params (WslcFeature Feature, WslcCapability Capability)[] features) =>
        new("wslc.exe", "2.9.12.0", features.ToDictionary(f => f.Feature, f => f.Capability));
}
