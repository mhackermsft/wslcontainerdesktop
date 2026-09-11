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

using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeCompatibilityPreviewTests
{
    [Theory]
    [InlineData(ComposeSettingDisposition.Supported, true, false)]
    [InlineData(ComposeSettingDisposition.Approximated, true, true)]
    [InlineData(ComposeSettingDisposition.Ignored, true, true)]
    [InlineData(ComposeSettingDisposition.Blocked, false, false)]
    public void ViewModelPreservesDispositionsAndCannotOverrideBlockers(
        ComposeSettingDisposition disposition, bool canApply, bool warnings)
    {
        var setting = new ComposeCompatibilitySetting("web", "setting", disposition, "value",
            "explanation", "source", WslcCapabilitySupport.Unknown);
        var preview = new ComposeCompatibilityPreview("demo", ComposeLifecycleOperation.Up, [setting]);
        var vm = new ComposePreviewViewModel(preview);

        Assert.Equal(canApply, vm.CanApply);
        Assert.Equal(warnings, vm.HasWarnings);
        Assert.Contains("demo", vm.Title);
        Assert.Equal("Apply reviewed plan", vm.ApplyLabel);
        Assert.Contains("Unknown", setting.Summary);
        Assert.Contains("Source: source", setting.Detail);
        Assert.Same(preview.Settings, vm.Settings);
        Assert.Null(typeof(ComposePreviewViewModel).GetProperty(nameof(vm.CanApply))!.SetMethod);
        if (!canApply) Assert.Contains("cannot be ignored", vm.Summary);
    }

    [Theory]
    [InlineData(ComposeLifecycleOperation.Restart)]
    [InlineData(ComposeLifecycleOperation.Stop)]
    [InlineData(ComposeLifecycleOperation.Down)]
    public void ExistingOperationLabelDoesNotOfferUp(ComposeLifecycleOperation operation)
    {
        var vm = new ComposePreviewViewModel(new("demo", operation, []));
        Assert.Equal("Confirm operation", vm.ApplyLabel);
        Assert.Contains(operation.ToString(), vm.Title);
    }

    [Theory]
    [InlineData("Unsupported option 'deploy.placement' is ignored.", ComposeSettingDisposition.Ignored)]
    [InlineData("Variable 'MISSING' is unset; using an empty string.", ComposeSettingDisposition.Blocked)]
    [InlineData("Blocked deployment: unresolved input.", ComposeSettingDisposition.Blocked)]
    public void ImportDiagnosticsKeepSeverityAndSource(string warning, ComposeSettingDisposition disposition)
    {
        var project = new ComposeProject { Name = "demo", Warnings = [warning] };
        var preview = new ComposePreviewProjection(project).Create(project, new(), new());
        var row = Assert.Single(preview.Settings);
        Assert.Equal(disposition, row.Disposition);
        Assert.Equal("Importer diagnostic", row.Source);
    }

    [Fact]
    public void ProjectionShowsLogicalServicesInstancesAndApplicationOwnership()
    {
        var project = ComposeImporter.ParseProject("""
            services:
              web:
                image: fixture
                scale: 2
                ports: ["80"]
                volumes: ["data:/data"]
                restart: always
                cpus: 2
                mem_limit: 512m
                depends_on: [db]
              db:
                image: fixture
            volumes:
              data: {}
            """);
        project.Name = "demo";
        var service = project.Services.Single(s => s.Name == "web");
        var plan = new ComposeReconciliationPlan([
            Entry(service) with { DesiredReplicas = 2, HealthOwner = ComposePolicyOwner.Application },
            Entry(service) with { InstanceIndex = 2, DesiredReplicas = 2, ContainerName = "demo_web_2" },
        ]);
        var preview = new ComposePreviewProjection(project).Create(project, new(), plan);

        Assert.True(preview.CanApply);
        Assert.Equal("2", Row("replicas").EffectiveValue);
        Assert.Contains("web#2", Row("instances").EffectiveValue);
        Assert.Contains("db: ServiceStarted", Row("depends_on").EffectiveValue);
        Assert.Contains("2", Row("resources").EffectiveValue);
        Assert.Contains("512", Row("resources").EffectiveValue);
        Assert.Contains("80", Row("ports").EffectiveValue);
        Assert.Contains("shared", Row("volumes").Explanation);
        Assert.Equal(ComposeSettingDisposition.Approximated, Row("healthcheck").Disposition);
        Assert.Contains("application", Row("restart").EffectiveValue);
        // The divergence that matters: app-owned supervision pauses while the app is not running.
        Assert.Contains("app is closed", Row("restart").Explanation);
        Assert.All(preview.Settings, row => Assert.NotEmpty(row.Source));

        ComposeCompatibilitySetting Row(string setting) => Assert.Single(preview.Settings, s => s.Setting == setting);
    }

    [Fact]
    public void PublicProjectionMasksContextSecretsAndUsesSharedSanitizer()
    {
        const string secret = "innocent-env-value-9572";
        var service = new ComposeService
        {
            Name = "web",
            Options = new()
            {
                Image = "name:registry-credential@registry.invalid/image",
                EnvironmentVariables = ["ORDINARY=" + secret],
                Labels = new() { ["custom"] = "label-value-9371", [ComposeProject.ServiceLabel] = "web" },
                Command = "echo command-payload-5149",
                Entrypoint = "entrypoint-payload-8731",
                User = "private-user-7931",
                Health = new() { Test = ["CMD-SHELL", "health-payload-6731"] },
            },
            Build = new() { Args = ["ARG=build-argument-4862"], Labels = new() { ["custom"] = "build-label-2468" } },
        };
        var project = new ComposeProject
        {
            Name = "demo", Services = [service],
            Networks = [new() { DriverOpts = ["key=private-network-driver-value"] }],
            Volumes = [new() { DriverOpts = ["key=private-volume-driver-value"] }],
            Warnings = [
                $"{secret} label-value-9371 echo command-payload-5149 entrypoint-payload-8731 private-user-7931 health-payload-6731 build-argument-4862 build-label-2468",
                @"C:\Users\private-person\file.yaml: SECRET=external-secret-value Bearer auth-value-8721",
            ],
        };
        project.Warnings.Add("private-network-driver-value private-volume-driver-value");
        var preview = new ComposePreviewProjection(project).Create(project, new(), new([Entry(service)]));
        var serialized = JsonSerializer.Serialize(preview);

        foreach (var privateValue in new[] { secret, "registry-credential", "label-value-9371",
            "command-payload-5149", "entrypoint-payload-8731", "private-user-7931", "health-payload-6731",
            "build-argument-4862", "build-label-2468", "private-person", "external-secret-value", "auth-value-8721",
            "private-network-driver-value", "private-volume-driver-value" })
            Assert.DoesNotContain(privateValue, serialized);
        Assert.Contains(preview.Settings, row => row.Service == "web");
        Assert.Contains("redacted", serialized);
    }

    [Fact]
    public void ProjectionSettingsCannotBeMutatedThroughCollectionInterface()
    {
        var project = new ComposeProject { Name = "demo" };
        var preview = new ComposePreviewProjection(project).Create(project, new(), new());
        var settings = Assert.IsAssignableFrom<IList<ComposeCompatibilitySetting>>(preview.Settings);
        Assert.Throws<NotSupportedException>(() => settings.Clear());
    }

    [Theory]
    [InlineData(ComposeLifecycleOperation.Restart)]
    [InlineData(ComposeLifecycleOperation.Stop)]
    [InlineData(ComposeLifecycleOperation.Down)]
    public void PendingImportWarningsDoNotBlockExistingLifecycle(ComposeLifecycleOperation operation)
    {
        var project = new ComposeProject { Name = "demo", Warnings = ["Blocked deployment: pending edit."] };
        var preview = new ComposePreviewProjection(project).Create(project, new() { Operation = operation }, new());
        Assert.True(preview.CanApply);
        Assert.DoesNotContain(preview.Settings, row => row.Setting == "import diagnostic");
    }

    [Fact]
    public void ImportOnlySerializationCannotEraseUnresolvedInputBlockers()
    {
        var project = new ComposeProject
        {
            Name = "demo", Warnings = ["Variable 'X' is unset; using an empty string."],
        };
        var restored = JsonSerializer.Deserialize<ComposeProject>(JsonSerializer.Serialize(project))!;
        var preview = new ComposePreviewProjection(restored).Create(restored, new(), new());
        Assert.False(preview.CanApply);
        Assert.Equal(project.Warnings, restored.Warnings);
    }

    private static ComposeServicePlan Entry(ComposeService service) => new(service, "demo_web", "",
        ComposeServiceChange.Missing, ComposeServiceAction.Create, "Missing instance.", null)
    {
        Backend = ComposeExecutionBackend.LegacyRun, HealthOwner = ComposePolicyOwner.None,
    };
}
