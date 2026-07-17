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
    IKubernetesService kubernetes,
    ITemplateCatalog templates,
    IComposeProjectStore composeStore,
    ComposeProjectSupervisor composeSupervisor)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<IReadOnlyList<AiToolDefinition>> GetDefinitionsAsync(CancellationToken ct)
    {
        var definitions = new List<AiToolDefinition>
        {
            Tool("list_containers", "List containers, including stopped containers.", "{}"),
            Tool("inspect_container", "Inspect a container by id or name.", ObjectSchema(("id", "string", "Container id or name"))),
            Tool("get_container_logs", "Get recent logs for a container.", ObjectSchema(("id", "string", "Container id or name"), ("tail", "integer", "Number of lines, max 1000"))),
            Tool("list_images", "List local images.", "{}"),
            Tool("list_volumes", "List volumes.", "{}"),
            Tool("list_networks", "List networks.", "{}"),
            Tool("engine_status", "Check WSL container engine availability and version.", "{}"),
            Tool("list_compose_projects", "List saved compose projects.", "{}"),
            Tool("run_container", "Run a container from structured options. Use for simple deployments such as nginx. Set gpus=true for GPU workloads (e.g. Ollama, CUDA).", RunContainerSchema()),
            Tool("pull_image", "Pull a container image reference.", ObjectSchema(("reference", "string", "Image reference, e.g. nginx:alpine"))),
            Tool("start_container", "Start a container by id or name.", ObjectSchema(("id", "string", "Container id or name"))),
            Tool("stop_container", "Stop a container by id or name.", ObjectSchema(("id", "string", "Container id or name"))),
            Tool("restart_container", "Restart a container by id or name.", ObjectSchema(("id", "string", "Container id or name"))),
            Tool("remove_container", "Remove a container by id or name.", ObjectSchema(("id", "string", "Container id or name"))),
            Tool("stop_all_containers", "Stop running containers. Set namePrefix or nameContains to restrict to matching names (e.g. namePrefix \"wordpress_\"); omit BOTH filters to stop every running container.", BulkContainerSchema(includeOnlyRunning: false)),
            Tool("remove_all_containers", "Remove containers after the app resolves the target list. Set namePrefix or nameContains to restrict to matching names (e.g. namePrefix \"wordpress_\"); omit BOTH filters to remove every container. Prefer this with a filter over multiple remove_container calls when the user names a group.", BulkContainerSchema(includeOnlyRunning: true)),
            Tool("deploy_template", "Deploy an app template by id or name. Available templates include: " + TemplateList(), ObjectSchema(("idOrName", "string", "Template id or name, e.g. wordpress"))),
            Tool("create_volume", "Create a named volume.", ObjectSchema(("name", "string", "Volume name"))),
            Tool("remove_volume", "Remove a named volume.", ObjectSchema(("name", "string", "Volume name"))),
            Tool("create_network", "Create a named network.", ObjectSchema(("name", "string", "Network name"))),
            Tool("remove_network", "Remove a named network.", ObjectSchema(("name", "string", "Network name"))),
        };

        try
        {
            var status = await kubernetes.GetStatusAsync(ct).ConfigureAwait(false);
            if (status.State is not ClusterState.NotInstalled)
            {
                definitions.AddRange([
                    Tool("k8s_status", "Get k3s cluster status.", "{}"),
                    Tool("list_k8s_resources", "List k3s resources by kind: pods, deployments, services, ingresses, pvc, configmaps, secrets, jobs, cronjobs, namespaces.", ObjectSchema(("kind", "string", "Resource kind"), ("namespace", "string", "Optional namespace"))),
                    Tool("get_k8s_logs", "Get recent logs for a pod.", ObjectSchema(("namespace", "string", "Namespace"), ("name", "string", "Pod name"), ("tail", "integer", "Number of lines"))),
                    Tool("apply_yaml", "Apply a Kubernetes YAML manifest.", ObjectSchema(("yaml", "string", "Kubernetes YAML manifest"))),
                    Tool("scale_deployment", "Scale a Kubernetes deployment.", ObjectSchema(("namespace", "string", "Namespace"), ("name", "string", "Deployment name"), ("replicas", "integer", "Replica count"))),
                    Tool("restart_deployment", "Restart a Kubernetes deployment.", ObjectSchema(("namespace", "string", "Namespace"), ("name", "string", "Deployment name"))),
                    Tool("delete_resource", "Delete a Kubernetes resource.", ObjectSchema(("kind", "string", "Resource kind"), ("namespace", "string", "Namespace or empty for cluster-scoped"), ("name", "string", "Resource name"))),
                    Tool("cluster_start", "Start k3s.", "{}"),
                    Tool("cluster_stop", "Stop k3s.", "{}"),
                ]);
            }
        }
        catch
        {
            // If status probing fails, do not expose k3s tools.
        }

        return definitions;
    }

    public async Task<AssistantResolvedToolCall> ResolveAsync(AiToolCall call, CancellationToken ct)
    {
        var args = ParseArgs(call.ArgumentsJson);
        return call.Name switch
        {
            "list_containers" => Resolved(call, AssistantPermissionCategory.ReadOnly, "List containers", "", token => ListContainersAsync(token)),
            "inspect_container" => Resolved(call, AssistantPermissionCategory.ReadOnly, $"Inspect {StringArg(args, "id")}", call.ArgumentsJson, token => InspectContainerAsync(StringArg(args, "id"), token)),
            "get_container_logs" => Resolved(call, AssistantPermissionCategory.ReadOnly, $"Get logs for {StringArg(args, "id")}", call.ArgumentsJson, token => GetContainerLogsAsync(StringArg(args, "id"), IntArg(args, "tail", 200), token)),
            "list_images" => Resolved(call, AssistantPermissionCategory.ReadOnly, "List images", "", token => ListImagesAsync(token)),
            "list_volumes" => Resolved(call, AssistantPermissionCategory.ReadOnly, "List volumes", "", token => ListVolumesAsync(token)),
            "list_networks" => Resolved(call, AssistantPermissionCategory.ReadOnly, "List networks", "", token => ListNetworksAsync(token)),
            "engine_status" => Resolved(call, AssistantPermissionCategory.ReadOnly, "Check engine status", "", token => EngineStatusAsync(token)),
            "list_compose_projects" => Resolved(call, AssistantPermissionCategory.ReadOnly, "List compose projects", "", _ => Task.FromResult(ListComposeProjects())),
            "run_container" => ResolveRunContainer(call, args),
            "pull_image" => Resolved(call, AssistantPermissionCategory.CreateRun, $"Pull image {StringArg(args, "reference")}", call.ArgumentsJson, token => PullImageAsync(StringArg(args, "reference"), token)),
            "start_container" => Resolved(call, AssistantPermissionCategory.Lifecycle, $"Start {StringArg(args, "id")}", call.ArgumentsJson, token => StartContainerAsync(StringArg(args, "id"), token)),
            "stop_container" => Resolved(call, AssistantPermissionCategory.Lifecycle, $"Stop {StringArg(args, "id")}", call.ArgumentsJson, token => StopContainerAsync(StringArg(args, "id"), token)),
            "restart_container" => Resolved(call, AssistantPermissionCategory.Lifecycle, $"Restart {StringArg(args, "id")}", call.ArgumentsJson, token => RestartContainerAsync(StringArg(args, "id"), token)),
            "remove_container" => Resolved(call, AssistantPermissionCategory.Destructive, $"Remove {StringArg(args, "id")}", call.ArgumentsJson, token => RemoveContainerAsync(StringArg(args, "id"), token)),
            "stop_all_containers" => await ResolveStopAllAsync(call, OptionalStringArg(args, "namePrefix"), OptionalStringArg(args, "nameContains"), ct).ConfigureAwait(false),
            "remove_all_containers" => await ResolveRemoveAllAsync(call, BoolArg(args, "onlyRunning", true), OptionalStringArg(args, "namePrefix"), OptionalStringArg(args, "nameContains"), ct).ConfigureAwait(false),
            "deploy_template" => Resolved(call, AssistantPermissionCategory.ComposeTemplate, $"Deploy template {StringArg(args, "idOrName")}", call.ArgumentsJson, token => DeployTemplateAsync(StringArg(args, "idOrName"), token)),
            "create_volume" => Resolved(call, AssistantPermissionCategory.CreateRun, $"Create volume {StringArg(args, "name")}", call.ArgumentsJson, token => CreateVolumeAsync(StringArg(args, "name"), token)),
            "remove_volume" => Resolved(call, AssistantPermissionCategory.Destructive, $"Remove volume {StringArg(args, "name")}", call.ArgumentsJson, token => RemoveVolumeAsync(StringArg(args, "name"), token)),
            "create_network" => Resolved(call, AssistantPermissionCategory.CreateRun, $"Create network {StringArg(args, "name")}", call.ArgumentsJson, token => CreateNetworkAsync(StringArg(args, "name"), token)),
            "remove_network" => Resolved(call, AssistantPermissionCategory.Destructive, $"Remove network {StringArg(args, "name")}", call.ArgumentsJson, token => RemoveNetworkAsync(StringArg(args, "name"), token)),
            "k8s_status" => Resolved(call, AssistantPermissionCategory.ReadOnly, "Get k3s status", "", K8sStatusAsync),
            "list_k8s_resources" => Resolved(call, AssistantPermissionCategory.ReadOnly, $"List k3s {StringArg(args, "kind")}", call.ArgumentsJson, token => ListK8sResourcesAsync(StringArg(args, "kind"), OptionalStringArg(args, "namespace"), token)),
            "get_k8s_logs" => Resolved(call, AssistantPermissionCategory.ReadOnly, $"Get k3s pod logs for {StringArg(args, "name")}", call.ArgumentsJson, token => GetK8sLogsAsync(OptionalStringArg(args, "namespace") ?? "default", StringArg(args, "name"), IntArg(args, "tail", 200), token)),
            "apply_yaml" => Resolved(call, AssistantPermissionCategory.Kubernetes, "Apply Kubernetes YAML", call.ArgumentsJson, token => ApplyYamlAsync(StringArg(args, "yaml"), token)),
            "scale_deployment" => Resolved(call, AssistantPermissionCategory.Kubernetes, $"Scale deployment {StringArg(args, "name")} to {IntArg(args, "replicas", 1)}", call.ArgumentsJson, token => ScaleDeploymentAsync(OptionalStringArg(args, "namespace") ?? "default", StringArg(args, "name"), IntArg(args, "replicas", 1), token)),
            "restart_deployment" => Resolved(call, AssistantPermissionCategory.Kubernetes, $"Restart deployment {StringArg(args, "name")}", call.ArgumentsJson, token => RestartDeploymentAsync(OptionalStringArg(args, "namespace") ?? "default", StringArg(args, "name"), token)),
            "delete_resource" => Resolved(call, AssistantPermissionCategory.Kubernetes, $"Delete {StringArg(args, "kind")} {StringArg(args, "name")}", call.ArgumentsJson, token => DeleteResourceAsync(StringArg(args, "kind"), OptionalStringArg(args, "namespace") ?? string.Empty, StringArg(args, "name"), token)),
            "cluster_start" => Resolved(call, AssistantPermissionCategory.Kubernetes, "Start k3s", "", ClusterStartAsync),
            "cluster_stop" => Resolved(call, AssistantPermissionCategory.Kubernetes, "Stop k3s", "", ClusterStopAsync),
            _ => throw new InvalidOperationException($"Tool '{call.Name}' is not allowed."),
        };
    }

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
    public async Task<(string Result, IReadOnlyList<string> Targets)> StopAllContainersAsync(string? namePrefix, string? nameContains, CancellationToken ct)
    {
        var targets = await StopAllContainersAsyncPreview(namePrefix, nameContains, ct).ConfigureAwait(false);
        var results = new List<string>();
        foreach (var target in targets)
        {
            results.Add($"{target}: {Summarize(await wslc.StopContainerAsync(target, ct).ConfigureAwait(false))}");
        }

        return (targets.Count == 0 ? "No matching running containers." : string.Join(Environment.NewLine, results), targets);
    }

    [Description("High-risk: remove containers after resolving the concrete target list.")]
    public async Task<(string Result, IReadOnlyList<string> Targets)> RemoveAllContainersAsync(bool onlyRunning, string? namePrefix, string? nameContains, CancellationToken ct)
    {
        var targets = await RemoveAllContainersAsyncPreview(onlyRunning, namePrefix, nameContains, ct).ConfigureAwait(false);
        var results = new List<string>();
        foreach (var target in targets)
        {
            results.Add($"{target}: {Summarize(await wslc.RemoveContainerAsync(target, force: true, ct).ConfigureAwait(false))}");
        }

        return (targets.Count == 0 ? "No matching containers." : string.Join(Environment.NewLine, results), targets);
    }

    public async Task<IReadOnlyList<string>> StopAllContainersAsyncPreview(string? namePrefix, string? nameContains, CancellationToken ct) =>
        (await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false))
        .Where(c => c.State.IsRunning())
        .Where(c => MatchesNameFilter(c.Name, namePrefix, nameContains))
        .Select(c => string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name)
        .ToList();

    public async Task<IReadOnlyList<string>> RemoveAllContainersAsyncPreview(bool onlyRunning, string? namePrefix, string? nameContains, CancellationToken ct) =>
        (await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false))
        .Where(c => !onlyRunning || c.State.IsRunning())
        .Where(c => MatchesNameFilter(c.Name, namePrefix, nameContains))
        .Select(c => string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name)
        .ToList();

    private static bool MatchesNameFilter(string? name, string? namePrefix, string? nameContains)
    {
        var containerName = name ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(namePrefix) &&
            !containerName.StartsWith(namePrefix.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(nameContains) &&
            containerName.IndexOf(nameContains.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return true;
    }

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

    public async Task<string> CreateVolumeAsync(string name, CancellationToken ct) =>
        Summarize(await wslc.CreateVolumeAsync(RequireValue(name, "volume"), ct: ct).ConfigureAwait(false));

    public async Task<string> RemoveVolumeAsync(string name, CancellationToken ct) =>
        Summarize(await wslc.RemoveVolumeAsync(RequireValue(name, "volume"), ct).ConfigureAwait(false));

    public async Task<string> CreateNetworkAsync(string name, CancellationToken ct) =>
        Summarize(await wslc.CreateNetworkAsync(RequireValue(name, "network"), ct: ct).ConfigureAwait(false));

    public async Task<string> RemoveNetworkAsync(string name, CancellationToken ct) =>
        Summarize(await wslc.RemoveNetworkAsync(RequireValue(name, "network"), ct).ConfigureAwait(false));

    public async Task<string> K8sStatusAsync(CancellationToken ct) =>
        JsonSerializer.Serialize(await kubernetes.GetStatusAsync(ct).ConfigureAwait(false), JsonOptions);

    public async Task<string> ListK8sResourcesAsync(string kind, string? ns, CancellationToken ct)
    {
        var normalized = RequireValue(kind, "kind").ToLowerInvariant();
        object resources = normalized switch
        {
            "pod" or "pods" => await kubernetes.GetPodsAsync(ns, ct).ConfigureAwait(false),
            "deployment" or "deployments" => await kubernetes.GetDeploymentsAsync(ns, ct).ConfigureAwait(false),
            "service" or "services" => await kubernetes.GetServicesAsync(ns, ct).ConfigureAwait(false),
            "ingress" or "ingresses" => await kubernetes.GetIngressesAsync(ns, ct).ConfigureAwait(false),
            "pvc" or "pvcs" or "persistentvolumeclaims" => await kubernetes.GetPvcsAsync(ns, ct).ConfigureAwait(false),
            "configmap" or "configmaps" => await kubernetes.GetConfigMapsAsync(ns, ct).ConfigureAwait(false),
            "secret" or "secrets" => await kubernetes.GetSecretsAsync(ns, ct).ConfigureAwait(false),
            "job" or "jobs" => await kubernetes.GetJobsAsync(ns, ct).ConfigureAwait(false),
            "cronjob" or "cronjobs" => await kubernetes.GetCronJobsAsync(ns, ct).ConfigureAwait(false),
            "namespace" or "namespaces" => await kubernetes.GetNamespacesAsync(ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported k3s resource kind '{kind}'."),
        };
        return JsonSerializer.Serialize(resources, JsonOptions);
    }

    public async Task<string> GetK8sLogsAsync(string ns, string name, int tail, CancellationToken ct) =>
        Summarize(await kubernetes.GetPodLogsAsync(ns, RequireValue(name, "pod"), Math.Clamp(tail, 1, 1000), ct).ConfigureAwait(false));

    public async Task<string> ApplyYamlAsync(string yaml, CancellationToken ct) =>
        Summarize(await kubernetes.ApplyManifestAsync(RequireValue(yaml, "yaml"), ct).ConfigureAwait(false));

    public async Task<string> ScaleDeploymentAsync(string ns, string name, int replicas, CancellationToken ct) =>
        Summarize(await kubernetes.ScaleDeploymentAsync(ns, RequireValue(name, "deployment"), Math.Clamp(replicas, 0, 100), ct).ConfigureAwait(false));

    public async Task<string> RestartDeploymentAsync(string ns, string name, CancellationToken ct) =>
        Summarize(await kubernetes.RestartDeploymentAsync(ns, RequireValue(name, "deployment"), ct).ConfigureAwait(false));

    public async Task<string> DeleteResourceAsync(string kind, string ns, string name, CancellationToken ct) =>
        Summarize(await kubernetes.DeleteResourceAsync(RequireValue(kind, "kind"), ns, RequireValue(name, "resource"), ct).ConfigureAwait(false));

    public async Task<string> ClusterStartAsync(CancellationToken ct) =>
        Summarize(await kubernetes.StartAsync(ct).ConfigureAwait(false));

    public async Task<string> ClusterStopAsync(CancellationToken ct) =>
        Summarize(await kubernetes.StopAsync(ct).ConfigureAwait(false));

    private string TemplateList() => string.Join(", ", templates.Templates.Select(t => $"{t.Id} ({t.Name})").Take(40));

    private static AiToolDefinition Tool(string name, string description, string schema) => new()
    {
        Name = name,
        Description = description,
        JsonSchemaParameters = schema == "{}" ? """{"type":"object","properties":{},"additionalProperties":false}""" : schema,
    };

    private static string ObjectSchema(params (string Name, string Type, string Description)[] properties)
    {
        var props = string.Join(",", properties.Select(p => $"\"{p.Name}\":{{\"type\":\"{p.Type}\",\"description\":{JsonSerializer.Serialize(p.Description)}}}"));
        var required = string.Join(",", properties.Where(p => !p.Name.Equals("namespace", StringComparison.OrdinalIgnoreCase) && !p.Name.Equals("tail", StringComparison.OrdinalIgnoreCase)).Select(p => JsonSerializer.Serialize(p.Name)));
        return $$"""{"type":"object","properties":{ {{props}} },"required":[{{required}}],"additionalProperties":false}""";
    }

    private static string BulkContainerSchema(bool includeOnlyRunning)
    {
        var onlyRunning = includeOnlyRunning
            ? "\"onlyRunning\":{\"type\":\"boolean\",\"description\":\"If true (default) only running containers are affected; if false, stopped containers too.\"},"
            : string.Empty;
        return "{\"type\":\"object\",\"properties\":{" +
               onlyRunning +
               "\"namePrefix\":{\"type\":\"string\",\"description\":\"Only affect containers whose name starts with this value, e.g. wordpress_. Omit to match all.\"}," +
               "\"nameContains\":{\"type\":\"string\",\"description\":\"Only affect containers whose name contains this substring. Omit to match all.\"}" +
               "},\"required\":[],\"additionalProperties\":false}";
    }

    private static string RunContainerSchema() => """
        {
          "type": "object",
          "properties": {
            "image": { "type": "string", "description": "Image reference" },
            "name": { "type": "string", "description": "Optional container name" },
            "command": { "type": "string", "description": "Optional command to run in the container" },
            "entrypoint": { "type": "string", "description": "Override the image entrypoint executable (--entrypoint)" },
            "ports": { "type": "array", "items": { "type": "string" }, "description": "host:container[/proto] port mappings (-p)" },
            "environment": { "type": "array", "items": { "type": "string" }, "description": "KEY=VALUE environment variables (-e)" },
            "volumes": { "type": "array", "items": { "type": "string" }, "description": "source:destination volume/bind mounts (-v)" },
            "labels": { "type": "array", "items": { "type": "string" }, "description": "KEY=VALUE metadata labels (--label)" },
            "networks": { "type": "array", "items": { "type": "string" }, "description": "Networks to attach; the first is the primary network (--network)" },
            "aliases": { "type": "array", "items": { "type": "string" }, "description": "Network-scoped hostname aliases (--network-alias)" },
            "dns": { "type": "array", "items": { "type": "string" }, "description": "DNS nameserver IPs (--dns)" },
            "dnsSearch": { "type": "array", "items": { "type": "string" }, "description": "DNS search domains (--dns-search)" },
            "dnsOptions": { "type": "array", "items": { "type": "string" }, "description": "DNS resolver options (--dns-option)" },
            "tmpfs": { "type": "array", "items": { "type": "string" }, "description": "tmpfs mount targets (--tmpfs)" },
            "ulimits": { "type": "array", "items": { "type": "string" }, "description": "ulimit settings as name=soft[:hard] (--ulimit)" },
            "gpus": { "type": "boolean", "description": "Set true to grant the container access to all host GPUs (maps to --gpus all). Use for GPU workloads such as Ollama or CUDA images." },
            "removeOnExit": { "type": "boolean", "description": "Remove the container automatically when it exits (--rm)" },
            "user": { "type": "string", "description": "User to run the process as (--user)" },
            "workingDir": { "type": "string", "description": "Working directory inside the container (--workdir)" },
            "hostname": { "type": "string", "description": "Container hostname (--hostname)" },
            "domainname": { "type": "string", "description": "Container domain name (--domainname)" },
            "cpuLimit": { "type": "string", "description": "CPU limit, e.g. \"1.5\" (--cpus)" },
            "memoryLimit": { "type": "string", "description": "Memory limit, e.g. \"512M\" (--memory)" },
            "shmSize": { "type": "string", "description": "Size of /dev/shm, e.g. \"64M\" (--shm-size)" },
            "stopSignal": { "type": "string", "description": "Signal used to stop the container (--stop-signal)" }
          },
          "required": ["image"],
          "additionalProperties": false
        }
        """;

    private AssistantResolvedToolCall ResolveRunContainer(AiToolCall call, JsonElement args)
    {
        var options = new RunContainerOptions
        {
            Image = StringArg(args, "image"),
            Name = OptionalStringArg(args, "name"),
            Command = OptionalStringArg(args, "command"),
            Entrypoint = OptionalStringArg(args, "entrypoint"),
            AllGpus = BoolArg(args, "gpus", false),
            RemoveOnExit = BoolArg(args, "removeOnExit", false),
            User = OptionalStringArg(args, "user"),
            WorkingDir = OptionalStringArg(args, "workingDir"),
            Hostname = OptionalStringArg(args, "hostname"),
            Domainname = OptionalStringArg(args, "domainname"),
            CpuLimit = OptionalStringArg(args, "cpuLimit"),
            MemoryLimit = OptionalStringArg(args, "memoryLimit"),
            ShmSize = OptionalStringArg(args, "shmSize"),
            StopSignal = OptionalStringArg(args, "stopSignal"),
        };
        AddStringArray(args, "ports", options.PortMappings);
        AddStringArray(args, "environment", options.EnvironmentVariables);
        AddStringArray(args, "volumes", options.Volumes);
        AddStringArray(args, "networks", options.Networks);
        AddStringArray(args, "aliases", options.Aliases);
        AddStringArray(args, "dns", options.Dns);
        AddStringArray(args, "dnsSearch", options.DnsSearch);
        AddStringArray(args, "dnsOptions", options.DnsOptions);
        AddStringArray(args, "tmpfs", options.Tmpfs);
        AddStringArray(args, "ulimits", options.Ulimits);
        AddKeyValueArray(args, "labels", options.Labels);
        return Resolved(call, AssistantPermissionCategory.CreateRun, $"Run container {options.Image}", JsonSerializer.Serialize(options, JsonOptions), token => RunContainerAsync(options, token));
    }

    private async Task<AssistantResolvedToolCall> ResolveStopAllAsync(AiToolCall call, string? namePrefix, string? nameContains, CancellationToken ct)
    {
        var targets = await StopAllContainersAsyncPreview(namePrefix, nameContains, ct).ConfigureAwait(false);
        var summary = DescribeBulkScope("Stop", "running containers", namePrefix, nameContains);
        return Resolved(call, AssistantPermissionCategory.Lifecycle, summary, BulkTargetDetails(targets), async token => (await StopAllContainersAsync(namePrefix, nameContains, token).ConfigureAwait(false)).Result);
    }

    private async Task<AssistantResolvedToolCall> ResolveRemoveAllAsync(AiToolCall call, bool onlyRunning, string? namePrefix, string? nameContains, CancellationToken ct)
    {
        var targets = await RemoveAllContainersAsyncPreview(onlyRunning, namePrefix, nameContains, ct).ConfigureAwait(false);
        var scope = onlyRunning ? "running containers" : "containers";
        var summary = DescribeBulkScope("Remove", scope, namePrefix, nameContains);
        return Resolved(call, AssistantPermissionCategory.Destructive, summary, BulkTargetDetails(targets), async token => (await RemoveAllContainersAsync(onlyRunning, namePrefix, nameContains, token).ConfigureAwait(false)).Result);
    }

    private static string DescribeBulkScope(string verb, string scope, string? namePrefix, string? nameContains)
    {
        if (!string.IsNullOrWhiteSpace(namePrefix))
        {
            return $"{verb} {scope} named \"{namePrefix.Trim()}*\"";
        }

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            return $"{verb} {scope} containing \"{nameContains.Trim()}\"";
        }

        return $"{verb} all {scope}";
    }

    private static string BulkTargetDetails(IReadOnlyList<string> targets) =>
        targets.Count == 0
            ? "No matching containers."
            : "Targets:\n" + string.Join(Environment.NewLine, targets);

    private static AssistantResolvedToolCall Resolved(
        AiToolCall call,
        AssistantPermissionCategory category,
        string summary,
        string details,
        Func<CancellationToken, Task<string>> execute) =>
        new(call, category, summary, details, execute);

    private static JsonElement ParseArgs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
    }

    private static string StringArg(JsonElement args, string name)
    {
        var value = OptionalStringArg(args, name);
        return RequireValue(value ?? string.Empty, name);
    }

    private static string? OptionalStringArg(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int IntArg(JsonElement args, string name, int fallback) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
            ? n
            : fallback;

    private static bool BoolArg(JsonElement args, string name, bool fallback) =>
        args.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static void AddStringArray(JsonElement args, string name, List<string> target)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                target.Add(item.GetString()!);
            }
        }
    }

    private static void AddKeyValueArray(JsonElement args, string name, Dictionary<string, string> target)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var raw = item.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var separator = raw.IndexOf('=');
            var key = separator >= 0 ? raw[..separator].Trim() : raw.Trim();
            var val = separator >= 0 ? raw[(separator + 1)..].Trim() : string.Empty;
            if (!string.IsNullOrEmpty(key))
            {
                target[key] = val;
            }
        }
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

public sealed record AssistantResolvedToolCall(
    AiToolCall Call,
    AssistantPermissionCategory Category,
    string Summary,
    string Details,
    Func<CancellationToken, Task<string>> ExecuteAsync);
