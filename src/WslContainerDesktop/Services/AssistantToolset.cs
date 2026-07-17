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

using System.ComponentModel;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed class AssistantToolset(
    IWslcService wslc,
    ITemplateCatalog templates,
    IComposeProjectStore composeStore,
    ComposeProjectSupervisor composeSupervisor)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Description("Read-only: list containers, including stopped containers.")]
    public async Task<string> ListContainersAsync(CancellationToken ct)
    {
        var containers = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(containers, JsonOptions);
    }

    [Description("Read-only: inspect a container by id or name.")]
    public async Task<string> InspectContainerAsync(string id, CancellationToken ct)
    {
        var result = await wslc.InspectContainerAsync(RequireValue(id, "container"), ct).ConfigureAwait(false);
        return Summarize(result);
    }

    [Description("Read-only: get recent container logs.")]
    public async Task<string> GetContainerLogsAsync(string id, int tail, CancellationToken ct)
    {
        var result = await wslc.GetLogsAsync(RequireValue(id, "container"), Math.Clamp(tail, 1, 1000), ct).ConfigureAwait(false);
        return Summarize(result);
    }

    [Description("Read-only: list local images.")]
    public async Task<string> ListImagesAsync(CancellationToken ct) =>
        JsonSerializer.Serialize(await wslc.ListImagesAsync(ct).ConfigureAwait(false), JsonOptions);

    [Description("Read-only: list volumes.")]
    public async Task<string> ListVolumesAsync(CancellationToken ct) =>
        JsonSerializer.Serialize(await wslc.ListVolumesAsync(ct).ConfigureAwait(false), JsonOptions);

    [Description("Read-only: list networks.")]
    public async Task<string> ListNetworksAsync(CancellationToken ct) =>
        JsonSerializer.Serialize(await wslc.ListNetworksAsync(ct).ConfigureAwait(false), JsonOptions);

    [Description("Read-only: get engine status.")]
    public async Task<string> EngineStatusAsync(CancellationToken ct)
    {
        var available = await wslc.IsEngineAvailableAsync(ct).ConfigureAwait(false);
        var version = available ? await wslc.GetVersionAsync(ct).ConfigureAwait(false) : null;
        return available ? $"Engine is available. {Summarize(version!)}" : "Engine is not available.";
    }

    [Description("Read-only: list saved compose projects.")]
    public string ListComposeProjects() =>
        JsonSerializer.Serialize(composeStore.GetAll().Select(p => new
        {
            p.Name,
            Services = p.Services.Select(s => s.Name).ToList(),
        }), JsonOptions);

    [Description("State-changing: run a new container from structured options.")]
    public async Task<string> RunContainerAsync(RunContainerOptions options, CancellationToken ct)
    {
        ValidateRunOptions(options);
        return Summarize(await wslc.RunContainerAsync(options, ct).ConfigureAwait(false));
    }

    [Description("State-changing: pull an image reference.")]
    public async Task<string> PullImageAsync(string reference, CancellationToken ct) =>
        Summarize(await wslc.PullImageAsync(RequireValue(reference, "image"), ct).ConfigureAwait(false));

    [Description("State-changing: start a container.")]
    public async Task<string> StartContainerAsync(string id, CancellationToken ct) =>
        Summarize(await wslc.StartContainerAsync(RequireValue(id, "container"), ct).ConfigureAwait(false));

    [Description("State-changing: stop a container.")]
    public async Task<string> StopContainerAsync(string id, CancellationToken ct) =>
        Summarize(await wslc.StopContainerAsync(RequireValue(id, "container"), ct).ConfigureAwait(false));

    [Description("State-changing: restart a container.")]
    public async Task<string> RestartContainerAsync(string id, CancellationToken ct) =>
        Summarize(await wslc.RestartContainerAsync(RequireValue(id, "container"), ct).ConfigureAwait(false));

    [Description("High-risk: remove a container.")]
    public async Task<string> RemoveContainerAsync(string id, CancellationToken ct) =>
        Summarize(await wslc.RemoveContainerAsync(RequireValue(id, "container"), force: true, ct).ConfigureAwait(false));

    [Description("High-risk: stop every currently running container.")]
    public async Task<(string Result, IReadOnlyList<string> Targets)> StopAllContainersAsync(CancellationToken ct)
    {
        var targets = await StopAllContainersAsyncPreview(ct).ConfigureAwait(false);
        var results = new List<string>();
        foreach (var target in targets)
        {
            results.Add($"{target}: {Summarize(await wslc.StopContainerAsync(target, ct).ConfigureAwait(false))}");
        }

        return (targets.Count == 0 ? "No running containers." : string.Join(Environment.NewLine, results), targets);
    }

    [Description("High-risk: remove containers after resolving the concrete target list.")]
    public async Task<(string Result, IReadOnlyList<string> Targets)> RemoveAllContainersAsync(bool onlyRunning, CancellationToken ct)
    {
        var targets = await RemoveAllContainersAsyncPreview(onlyRunning, ct).ConfigureAwait(false);
        var results = new List<string>();
        foreach (var target in targets)
        {
            results.Add($"{target}: {Summarize(await wslc.RemoveContainerAsync(target, force: true, ct).ConfigureAwait(false))}");
        }

        return (targets.Count == 0 ? "No matching containers." : string.Join(Environment.NewLine, results), targets);
    }

    public async Task<IReadOnlyList<string>> StopAllContainersAsyncPreview(CancellationToken ct) =>
        (await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false))
        .Where(c => c.State.IsRunning())
        .Select(c => string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name)
        .ToList();

    public async Task<IReadOnlyList<string>> RemoveAllContainersAsyncPreview(bool onlyRunning, CancellationToken ct) =>
        (await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false))
        .Where(c => !onlyRunning || c.State.IsRunning())
        .Select(c => string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name)
        .ToList();

    [Description("State-changing: deploy a built-in or user template by id/name.")]
    public async Task<string> DeployTemplateAsync(string idOrName, CancellationToken ct)
    {
        var key = RequireValue(idOrName, "template");
        var template = templates.Templates.FirstOrDefault(t =>
            string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Name, key, StringComparison.OrdinalIgnoreCase));
        if (template is null)
        {
            return $"Template '{key}' was not found.";
        }

        if (template.Kind == StackTemplateKind.Compose)
        {
            var yaml = template.ComposeYaml;
            if (string.IsNullOrWhiteSpace(yaml))
            {
                return $"Template '{template.Name}' has no compose YAML.";
            }

            var project = ComposeImporter.ParseProject(yaml);
            project.Name = string.IsNullOrWhiteSpace(template.ComposeProjectName)
                ? template.Id
                : template.ComposeProjectName;
            var up = await composeSupervisor.UpAsync(project, ct).ConfigureAwait(false);
            return $"Deployed compose template '{template.Name}'. Started {up.Started}/{up.Services.Count} services.";
        }

        if (template.RunOptions is null)
        {
            return $"Template '{template.Name}' has no run options.";
        }

        return Summarize(await wslc.RunContainerAsync(template.RunOptions.Clone(), ct).ConfigureAwait(false));
    }

    public static RunContainerOptions CreateNginxHelloWorldOptions() => new()
    {
        Image = "nginx:alpine",
        Name = "ai-nginx",
        PortMappings = { "8080:80" },
    };

    public static RunContainerOptions CreateHelloWorldOptions() => new()
    {
        Image = "hello-world:latest",
        Name = "hello-world",
    };

    public static string Summarize(CommandResult result)
    {
        var text = result.Success ? result.StandardOutput : result.ErrorText;
        text = string.IsNullOrWhiteSpace(text) ? (result.Success ? "Succeeded." : "Failed.") : text.Trim();
        return text.Length <= 4000 ? text : text[..4000] + "…";
    }

    private static void ValidateRunOptions(RunContainerOptions options)
    {
        if (options is null || string.IsNullOrWhiteSpace(options.Image))
        {
            throw new InvalidOperationException("A container image is required.");
        }
    }

    private static string RequireValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"A {name} value is required.");
        }

        return value.Trim();
    }

}
