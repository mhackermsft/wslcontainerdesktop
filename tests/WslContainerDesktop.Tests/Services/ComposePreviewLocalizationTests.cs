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

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

[Collection("UiText")]
public sealed class ComposePreviewLocalizationTests : IDisposable
{
    private readonly Dictionary<string, string> _english = ReadCatalog("en-US");
    private readonly Dictionary<string, string> _chinese = ReadCatalog("zh-Hans");

    [Theory]
    [InlineData("en-US", ComposeServiceAction.Create)]
    [InlineData("zh-Hans", ComposeServiceAction.Create)]
    [InlineData("en-US", ComposeServiceAction.Recreate)]
    [InlineData("zh-Hans", ComposeServiceAction.Recreate)]
    public void ActualProjectionLocalizesDisplayWithoutChangingRawEvidence(string language, ComposeServiceAction action)
    {
        ConfigureLanguage(language);
        var web = Service("web", "registry.invalid/shared:latest");
        web.Options.PortMappings.Add("8080:80");
        var project = new ComposeProject { Name = "demo", Services = [web] };
        var plan = new ComposeReconciliationPlan([Entry(web, ComposeImageAction.Pull, action)]);
        var preview = new ComposePreviewProjection(project).Create(project, new(), plan);
        var rawEvidence = JsonSerializer.Serialize(preview);
        var model = new ComposePreviewViewModel(preview);

        Assert.StartsWith(language == "zh-Hans" ? "检查 Compose " : "Review Compose ", model.Title);
        Assert.EndsWith(language == "zh-Hans" ? "：demo" : ": demo", model.Title);
        Assert.Equal(language == "zh-Hans" ? "应用已检查的计划" : "Apply reviewed plan", model.ApplyLabel);
        Assert.Contains(language == "zh-Hans" ? "1 个服务（web）" : "1 service (web)", model.Outcome);
        Assert.Contains(language == "zh-Hans" ? "开放 8080:80" : "publishes 8080:80", model.Outcome);
        Assert.Equal(preview.CanApply, model.CanApply);
        Assert.Equal(preview.HasWarnings, model.HasWarnings);
        Assert.Same(preview.Settings, model.Settings);
        foreach (var setting in model.Settings)
        {
            Assert.Contains(setting.Service + " · " + setting.Setting, setting.DisplaySummary);
            Assert.Contains(language == "zh-Hans" ? "来源：" : "Source: ", setting.DisplayDetail);
        }
        Assert.Contains(web.Options.Image, model.Settings.Single(setting => setting.Setting == "image").DisplayDetail);
        Assert.Equal(rawEvidence, JsonSerializer.Serialize(preview));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-Hans")]
    public void OutcomeLocalizesApplicationTextUsingUnmodifiedRawSettings(string language)
    {
        ConfigureLanguage(language);
        var preview = new ComposeCompatibilityPreview("demo", ComposeLifecycleOperation.Up,
        [
            new("web", "instances", ComposeSettingDisposition.Supported, "demo_web_1", "", "Fixture"),
            new("web", "image", ComposeSettingDisposition.Supported, "registry.invalid/image:latest", "Pull this image.", "Fixture"),
            new("web", "ports", ComposeSettingDisposition.Supported, "8080:80", "", "Fixture"),
        ]);
        var rawEvidence = JsonSerializer.Serialize(preview);

        var outcome = new ComposePreviewViewModel(preview).Outcome;

        Assert.Equal(language == "zh-Hans"
            ? "1 个服务（web） · 下载 1 个镜像（registry.invalid/image:latest） · 开放 8080:80."
            : "1 service (web) · downloads 1 image (registry.invalid/image:latest) · publishes 8080:80.", outcome);
        Assert.Equal(rawEvidence, JsonSerializer.Serialize(preview));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-Hans")]
    public void InlineConfigValidationUsesTheSelectedCatalogWithoutExposingContent(string language)
    {
        ConfigureLanguage(language);
        var catalog = language == "zh-Hans" ? _chinese : _english;
        var project = ComposeInlineConfigTests.InlineProject("synthetic-private-inline");
        project.Configs[0].File = "settings.json";

        var invalid = Assert.Throws<InvalidOperationException>(() => ComposeConfigContent.Validate(project));
        Assert.Equal(catalog["Compose_InlineConfigInvalid"], invalid.Message);
        Assert.DoesNotContain(project.Configs[0].Content!, invalid.Message);

        var source = Assert.Throws<ComposeConfigurationException>(() => ComposeImporter.ParseProject("""
            services: {web: {image: fixture}}
            configs: {settings: {content: [synthetic-private-inline]}}
            """));
        Assert.Contains(catalog["Compose_ConfigSourceInvalid"], source.Message);
        Assert.DoesNotContain("synthetic-private-inline", source.Message);

        project.Configs[0].File = null;
        project.Configs[0].Content = new string('x', 8_000_001);
        var limit = Assert.Throws<InvalidOperationException>(() => ComposeConfigContent.Validate(project));
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, catalog["Compose_InlineConfigLimit"], 8_000_000),
            limit.Message);
    }

    [Fact]
    public void InlineConfigLanguageSwitchPreservesContentFingerprintsAndExistingPreviewEvidence()
    {
        ConfigureLanguage("en-US");
        const string content = "{\"Name\":\"本地数据库 ☃\",\"Host\":\"postgres\"}\r\n";
        var project = ComposeImporter.ParseProject("""
            services:
              web:
                image: fixture
                configs:
                  - source: settings
                    target: /pgadmin4/servers.json
            configs:
              settings:
                content: '${WCD_INLINE_VALUE}'
            """, new Dictionary<string, string> { ["WCD_INLINE_VALUE"] = content });
        var serializedProject = JsonSerializer.Serialize(project);
        var fingerprint = ComposeReconciliationPlanner.Fingerprint(project, project.Services[0]);
        var request = new ComposeOperationRequest();
        var plan = ComposeReconciliationPlanner.Plan(project, request, [], new Dictionary<string, ContainerNetworkState>());
        var preview = new ComposePreviewProjection(project).Create(project, request, plan);
        var serializedPreview = JsonSerializer.Serialize(preview);
        var model = new ComposePreviewViewModel(preview);

        foreach (var language in new[] { "en-US", "zh-Hans", "en-US" })
        {
            ConfigureLanguage(language);

            Assert.StartsWith(language == "zh-Hans" ? "检查 Compose " : "Review Compose ", model.Title);
            Assert.Equal(language == "zh-Hans" ? "应用已检查的计划" : "Apply reviewed plan", model.ApplyLabel);
            Assert.Same(preview.Settings, model.Settings);
            Assert.Equal(preview.CanApply, model.CanApply);
            Assert.Contains(model.Settings, row => row.Setting == "configs" &&
                row.DisplayDetail.Contains("/pgadmin4/servers.json", StringComparison.Ordinal));
            Assert.Equal(serializedProject, JsonSerializer.Serialize(project));
            Assert.Equal(serializedPreview, JsonSerializer.Serialize(preview));
            Assert.Equal(fingerprint, ComposeReconciliationPlanner.Fingerprint(project, project.Services[0]));
            Assert.Equal(Encoding.UTF8.GetBytes(content), ComposeConfigContent.GetBytes(project.Configs[0].Content!));
            Assert.DoesNotContain(content, serializedPreview);
        }
    }

    private void ConfigureLanguage(string language)
    {
        var catalog = language == "zh-Hans" ? _chinese : _english;
        UiText.SetLanguage(language);
        UiText.Configure((key, args) => catalog.TryGetValue(key, out var value)
                ? string.Format(CultureInfo.InvariantCulture, value, args) : key,
            _english.Select(pair => (pair.Key, pair.Value, _chinese[pair.Key])));
    }

    private static ComposeService Service(string name, string image) => new()
    {
        Name = name, Options = new() { Image = image },
    };

    private static ComposeServicePlan Entry(ComposeService service, ComposeImageAction imageAction,
        ComposeServiceAction action) => new(service, "demo_" + service.Name, "",
        action == ComposeServiceAction.Recreate ? ComposeServiceChange.Changed : ComposeServiceChange.Missing,
        action, "Fixture instance.", action == ComposeServiceAction.Create ? null : "existing_" + service.Name)
    {
        ImageAction = imageAction, Backend = ComposeExecutionBackend.LegacyRun, HealthOwner = ComposePolicyOwner.None,
    };

    private static Dictionary<string, string> ReadCatalog(string language)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WslContainerDesktop.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory.FullName, "src", "WslContainerDesktop", "Strings", language, "Resources.resw"))
            .Root!.Elements("data").ToDictionary(element => element.Attribute("name")!.Value,
                element => element.Element("value")!.Value, StringComparer.Ordinal);
    }

    public void Dispose()
    {
        UiText.Configure(null);
        UiText.SetLanguage("en-US");
    }
}
