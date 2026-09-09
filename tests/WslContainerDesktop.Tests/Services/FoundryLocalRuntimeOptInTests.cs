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

/// <summary>
/// No discovery of a runtime, PATH execution, load/unload, acquisition, or default destination.
/// The operator must separately audit/install prerequisites and configure an exact model ID.
/// These checks do not prove packaged activation, GPU/NPU support, or artifact license compliance.
/// </summary>
public sealed class FoundryLocalRuntimeOptInTests
{
    [FoundryRuntimeFact("WSLC_FOUNDRY_LOCAL_METADATA_TESTS")]
    public async Task ExplicitEndpoint_MetadataOnly()
    {
        var settings = Settings();
        var configuration = AiConversationContext.Capture(settings, AiProviderKind.FoundryLocal);
        FoundryLocalRuntimeService.Validate(configuration);
        using var http = new FoundryLocalHttpClient();
        var runtime = new FoundryLocalRuntimeService(http, settings);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await runtime.ReadInventoryAsync(configuration, deadline.Token);
        Assert.NotNull(result.Selected);
        Assert.Equal(configuration, result.Configuration);
    }

    [FoundryRuntimeFact("WSLC_FOUNDRY_LOCAL_INFERENCE_TESTS")]
    public async Task ExplicitOptIn_AlreadyLoadedModel_SyntheticProbesAndDiagnosisOnly()
    {
        var settings = Settings();
        var configuration = AiConversationContext.Capture(settings, AiProviderKind.FoundryLocal);
        FoundryLocalRuntimeService.Validate(configuration);
        using var http = new FoundryLocalHttpClient();
        var runtime = new FoundryLocalRuntimeService(http, settings);
        var observer = new FoundryLocalCapabilityObserver(runtime, http);
        var capabilities = new AiCapabilityService([observer], new AiContractHarness.Credentials(null));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var metadata = await observer.ReadMetadataAsync(configuration, deadline.Token);
        Assert.Equal(AiLoadState.Loaded, metadata.Load); // Refuse an implicit cold load.
        Assert.Equal(AiDownloadState.Downloaded, metadata.Download);
        var proof = await capabilities.GetAsync(configuration, true, deadline.Token);
        Assert.True(proof.CanChat);
        var provider = new FoundryLocalProvider(http, settings, runtime, capabilities);
        var diagnosis = await provider.CompleteAsync(new("Return JSON only.",
            "Return {\"summary\":\"ok\",\"likelyCause\":\"synthetic test\",\"evidenceCited\":[],\"suggestedFix\":{\"description\":\"none\",\"commands\":[],\"fileEdits\":[]},\"confidence\":1}"),
            deadline.Token);
        Assert.False(string.IsNullOrWhiteSpace(diagnosis.Summary));
    }

    private static ISettingsService Settings() => NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
    {
        "get_AiProvider" => AiProviderKind.FoundryLocal,
        "get_AiFeaturesEnabled" => true,
        "get_AiFoundryLocalEndpoint" => Environment.GetEnvironmentVariable("WSLC_FOUNDRY_LOCAL_ENDPOINT") ?? "",
        "get_AiFoundryLocalModel" => Environment.GetEnvironmentVariable("WSLC_FOUNDRY_LOCAL_MODEL") ?? "",
        _ => throw new InvalidOperationException("Unexpected runtime-test dependency."),
    });
}

public sealed class FoundryRuntimeFactAttribute : FactAttribute
{
    public FoundryRuntimeFactAttribute(string gate)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(gate) != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSLC_FOUNDRY_LOCAL_ENDPOINT"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSLC_FOUNDRY_LOCAL_MODEL")))
            Skip = $"Requires Windows, {gate}=1, and explicit WSLC_FOUNDRY_LOCAL_ENDPOINT / WSLC_FOUNDRY_LOCAL_MODEL. No runtime is acquired or loaded.";
    }
}
