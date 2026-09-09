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
    ComposeProjectSupervisor composeSupervisor,
    ISettingsService settings,
    IRegistryCatalogService registryCatalog) : IAssistantToolset
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<IReadOnlyList<AiToolDefinition>> GetDefinitionsAsync(CancellationToken ct)
    {
        var definitions = new List<AiToolDefinition>
        {
            Tool("list_containers", "List containers, including stopped containers."),
            Tool("inspect_container", "Inspect a container by id or name."),
            Tool("get_container_logs", "Get recent logs for a container."),
            Tool("list_images", "List local images."),
            Tool("list_volumes", "List volumes."),
            Tool("list_networks", "List networks."),
            Tool("engine_status", "Check WSL container engine availability and version."),
            Tool("list_compose_projects", "List saved compose projects."),
            Tool("run_container", "Run a container from structured options. Use for simple deployments such as nginx. Set gpus=true for GPU workloads (e.g. Ollama, CUDA)."),
            Tool("pull_image", "Pull a container image reference."),
            Tool("start_container", "Start a container by id or name."),
            Tool("stop_container", "Stop a container by id or name."),
            Tool("restart_container", "Restart a container by id or name."),
            Tool("remove_container", "Remove a container by id or name."),
            Tool("stop_all_containers", "Stop running containers. Set namePrefix or nameContains to restrict matching names, or explicitly set scope=\"all\" without filters for every running container."),
            Tool("remove_all_containers", "Remove containers after the app resolves exact targets. Set namePrefix or nameContains to restrict matching names, or explicitly set scope=\"all\" without filters. onlyRunning defaults to true."),
            Tool("deploy_template", "Deploy an app template by id or name. Available templates include: " + TemplateList()),
            Tool("deploy_compose", "Deploy a multi-container application from a docker-compose YAML as a single project. ALWAYS use this (or deploy_template) for apps with more than one container (e.g. app + database, app + cache) so services share a network and can resolve each other by service name over DNS. Do NOT wire multiple run_container calls together."),
            Tool("create_volume", "Create a named volume."),
            Tool("remove_volume", "Remove a named volume."),
            Tool("create_network", "Create a named network."),
            Tool("remove_network", "Remove a named network."),
        };

        var browsableRegistries = settings.Registries.Where(registryCatalog.CanBrowse).ToList();
        if (browsableRegistries.Count > 0)
        {
            var names = string.Join(", ", browsableRegistries.Select(r => string.IsNullOrWhiteSpace(r.Name) ? r.Host : r.Name));
            definitions.Add(Tool("list_registry_repositories",
                "List the repositories (image names) available in a configured remote registry. Browsable registries: " + names + ". Docker Hub's global catalog is not browsable."));
            definitions.Add(Tool("list_registry_tags",
                "List the available tags (versions) of a repository in a configured remote registry, so you can see what versions exist and which is newest. Browsable registries: " + names + "."));
        }

        try
        {
            var status = await kubernetes.GetStatusAsync(ct).ConfigureAwait(false);
            if (status.State is not ClusterState.NotInstalled)
            {
                definitions.AddRange([
                    Tool("k8s_status", "Get k3s cluster status."),
                    Tool("list_k8s_resources", "List k3s resources by kind: pods, deployments, services, ingresses, pvc, configmaps, secrets, jobs, cronjobs, namespaces."),
                    Tool("get_k8s_logs", "Get recent logs for a pod."),
                    Tool("apply_yaml", "Apply a Kubernetes YAML manifest."),
                    Tool("scale_deployment", "Scale a Kubernetes deployment."),
                    Tool("restart_deployment", "Restart a Kubernetes deployment."),
                    Tool("delete_resource", "Delete a Kubernetes resource. Namespace may be empty for a cluster-scoped resource."),
                    Tool("cluster_start", "Start k3s."),
                    Tool("cluster_stop", "Stop k3s."),
                ]);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // If status probing fails, do not expose k3s tools.
        }

        return definitions;
    }

    public async Task<AssistantResolvedToolCall> ResolveAsync(AiToolCall call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var args = ValidateArgs(call);
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
            "start_container" or "stop_container" or "restart_container" or "remove_container" =>
                await ResolveContainerAsync(call, StringArg(args, "id"), ct).ConfigureAwait(false),
            "stop_all_containers" => await ResolveStopAllAsync(call, OptionalStringArg(args, "namePrefix"), OptionalStringArg(args, "nameContains"), ct).ConfigureAwait(false),
            "remove_all_containers" => await ResolveRemoveAllAsync(call, BoolArg(args, "onlyRunning", true), OptionalStringArg(args, "namePrefix"), OptionalStringArg(args, "nameContains"), ct).ConfigureAwait(false),
            "deploy_template" => Resolved(call, AssistantPermissionCategory.ComposeTemplate, $"Deploy template {StringArg(args, "idOrName")}", call.ArgumentsJson, token => DeployTemplateAsync(StringArg(args, "idOrName"), token)),
            "deploy_compose" => ResolveDeployCompose(call, args),
            "create_volume" => Resolved(call, AssistantPermissionCategory.CreateRun, $"Create volume {StringArg(args, "name")}", call.ArgumentsJson, token => CreateVolumeAsync(StringArg(args, "name"), token)),
            "remove_volume" => Resolved(call, AssistantPermissionCategory.Destructive, $"Remove volume {StringArg(args, "name")}", call.ArgumentsJson, token => RemoveVolumeAsync(StringArg(args, "name"), token)),
            "create_network" => Resolved(call, AssistantPermissionCategory.CreateRun, $"Create network {StringArg(args, "name")}", call.ArgumentsJson, token => CreateNetworkAsync(StringArg(args, "name"), token)),
            "remove_network" => Resolved(call, AssistantPermissionCategory.Destructive, $"Remove network {StringArg(args, "name")}", call.ArgumentsJson, token => RemoveNetworkAsync(StringArg(args, "name"), token)),
            "list_registry_repositories" => Resolved(call, AssistantPermissionCategory.ReadOnly, $"List repositories in registry {StringArg(args, "registry")}", call.ArgumentsJson, token => ListRegistryRepositoriesAsync(StringArg(args, "registry"), token)),
            "list_registry_tags" => Resolved(call, AssistantPermissionCategory.ReadOnly, $"List tags of {StringArg(args, "repository")} in registry {StringArg(args, "registry")}", call.ArgumentsJson, token => ListRegistryTagsAsync(StringArg(args, "registry"), StringArg(args, "repository"), token)),
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
            project.ApplyProjectNamespacing();
            var up = await composeSupervisor.UpAsync(project, ct).ConfigureAwait(false);
            return SummarizeCompose($"template '{template.Name}'", up);
        }

        if (template.RunOptions is null)
        {
            return $"Template '{template.Name}' has no run options.";
        }

        return Summarize(await wslc.RunContainerAsync(template.RunOptions.Clone(), ct).ConfigureAwait(false));
    }

    [Description("State-changing: deploy a multi-container app from a docker-compose YAML as a single project (shared network + DNS).")]
    public async Task<string> DeployComposeAsync(string yaml, string? projectName, CancellationToken ct)
    {
        var text = RequireValue(yaml, "compose YAML");
        var project = ComposeImporter.ParseProject(text);
        if (project.Services.Count == 0)
        {
            return "The compose YAML defines no services.";
        }

        project.Name = ResolveComposeProjectName(projectName, project.Name);
        project.ApplyProjectNamespacing();
        composeStore.Save(project);
        var up = await composeSupervisor.UpAsync(project, ct).ConfigureAwait(false);
        return SummarizeCompose($"project '{project.Name}'", up);
    }

    private static string SummarizeCompose(string target, ComposeUpResult result)
    {
        var status = result.IsCancelled ? "Cancelled" : result.AllSucceeded ? "Applied" : "Partially applied or failed";
        var outcomes = string.Join("\n", result.Services.Select(service =>
            $"{service.InstanceKey}: {(service.Success ? "succeeded" : "failed")} - {service.Detail}"));
        return $"{status} compose {target}. Started {result.Started} instances. Per-instance outcomes:\n{outcomes}";
    }

    private AssistantResolvedToolCall ResolveDeployCompose(AiToolCall call, JsonElement args)
    {
        var yaml = StringArg(args, "yaml");
        var projectName = OptionalStringArg(args, "projectName");
        var preview = ComposeImporter.ParseProject(yaml);
        if (preview.Services.Count == 0)
            throw new InvalidOperationException("Invalid tool arguments: compose YAML defines no services.");
        var name = ResolveComposeProjectName(projectName, preview.Name);
        var summary = $"Deploy compose project '{name}'";
        var details = "Services:\n" + string.Join(Environment.NewLine, preview.Services.Select(s => $"- {s.Name} ({s.Options.Image})"));

        return Resolved(call, AssistantPermissionCategory.ComposeTemplate, summary, details, token => DeployComposeAsync(yaml, projectName, token));
    }

    private static string ResolveComposeProjectName(string? requested, string? parsed)
    {
        var candidate = !string.IsNullOrWhiteSpace(requested) ? requested : parsed;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "ai-compose";
        }

        var sanitized = new string(candidate.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "ai-compose" : sanitized;
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
        return AiTextSanitizer.Sanitize(text, 4000);
    }

    public async Task<string> CreateVolumeAsync(string name, CancellationToken ct) =>
        Summarize(await wslc.CreateVolumeAsync(RequireValue(name, "volume"), ct: ct).ConfigureAwait(false));

    public async Task<string> RemoveVolumeAsync(string name, CancellationToken ct) =>
        Summarize(await wslc.RemoveVolumeAsync(RequireValue(name, "volume"), ct).ConfigureAwait(false));

    public async Task<string> CreateNetworkAsync(string name, CancellationToken ct) =>
        Summarize(await wslc.CreateNetworkAsync(RequireValue(name, "network"), ct: ct).ConfigureAwait(false));

    public async Task<string> RemoveNetworkAsync(string name, CancellationToken ct) =>
        Summarize(await wslc.RemoveNetworkAsync(RequireValue(name, "network"), ct).ConfigureAwait(false));

    [Description("Read-only: list repositories available in a configured remote registry.")]
    public async Task<string> ListRegistryRepositoriesAsync(string registry, CancellationToken ct)
    {
        var (entry, error) = ResolveBrowsableRegistry(registry);
        if (entry is null)
        {
            return error!;
        }

        var result = await registryCatalog.ListRepositoriesAsync(entry, ct).ConfigureAwait(false);
        return DescribeCatalogResult(result, $"repositories in registry '{DisplayName(entry)}'");
    }

    [Description("Read-only: list the tags/versions of a repository in a configured remote registry.")]
    public async Task<string> ListRegistryTagsAsync(string registry, string repository, CancellationToken ct)
    {
        var (entry, error) = ResolveBrowsableRegistry(registry);
        if (entry is null)
        {
            return error!;
        }

        var repo = RequireValue(repository, "repository");
        var result = await registryCatalog.ListTagsAsync(entry, repo, ct).ConfigureAwait(false);
        return DescribeCatalogResult(result, $"tags of '{repo}' in registry '{DisplayName(entry)}'");
    }

    private (RegistryEntry? Entry, string? Error) ResolveBrowsableRegistry(string registry)
    {
        var browsable = settings.Registries.Where(registryCatalog.CanBrowse).ToList();
        if (browsable.Count == 0)
        {
            return (null, JsonSerializer.Serialize(new
            {
                error = "No browsable registries are configured. Add an Azure Container Registry or a private Docker Registry v2 host on the Registries page (Docker Hub's global catalog cannot be browsed)."
            }, JsonOptions));
        }

        var query = registry?.Trim() ?? string.Empty;
        RegistryEntry? match = null;
        if (query.Length > 0)
        {
            match = browsable.FirstOrDefault(r =>
                string.Equals(r.Name, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Host, query, StringComparison.OrdinalIgnoreCase));
        }
        else if (browsable.Count == 1)
        {
            match = browsable[0];
        }

        if (match is null)
        {
            return (null, JsonSerializer.Serialize(new
            {
                error = query.Length > 0
                    ? $"No browsable registry matches '{query}'."
                    : "Multiple registries are configured; specify which one to browse.",
                available = browsable.Select(DisplayName).ToArray()
            }, JsonOptions));
        }

        return (match, null);
    }

    private string DescribeCatalogResult(CatalogResult result, string what)
    {
        if (result.IsOk)
        {
            return JsonSerializer.Serialize(new { count = result.Items.Count, items = result.Items }, JsonOptions);
        }

        return JsonSerializer.Serialize(new { error = result.Message ?? $"Could not list {what}." }, JsonOptions);
    }

    private static string DisplayName(RegistryEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Name) ? entry.Host : entry.Name;

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

    private static AiToolDefinition Tool(string name, string description) => new()
    {
        Name = name,
        Description = description,
        JsonSchemaParameters = ArgumentSchema(name),
    };

    private static string ObjectSchema(params (string Name, string Type, string Description)[] properties)
    {
        var props = string.Join(",", properties.Select(p => $"\"{p.Name}\":{{\"type\":\"{p.Type}\",\"description\":{JsonSerializer.Serialize(string.IsNullOrEmpty(p.Description) ? FieldDescription(p.Name) : p.Description)}{(p.Name == "tail" ? ",\"minimum\":1,\"maximum\":1000" : p.Name == "replicas" ? ",\"minimum\":0,\"maximum\":100" : "")}}}"));
        var required = string.Join(",", properties.Where(p => !p.Name.Equals("namespace", StringComparison.OrdinalIgnoreCase) && !p.Name.Equals("tail", StringComparison.OrdinalIgnoreCase)).Select(p => JsonSerializer.Serialize(p.Name)));
        return $$"""{"type":"object","properties":{ {{props}} },"required":[{{required}}],"additionalProperties":false}""";
    }

    private static string FieldDescription(string name) => name switch
    {
        "id" => "Container id or name",
        "tail" => "Number of log lines (1-1000); defaults to 200",
        "reference" => "Image reference, e.g. nginx:alpine",
        "idOrName" => "Template id or name, e.g. wordpress",
        "registry" => "Configured registry name or host",
        "repository" => "Repository/image name within the registry, e.g. team/myapp",
        "namespace" => "Optional namespace; defaults depend on the resource operation",
        "replicas" => "Replica count (0-100)",
        "kind" => "Resource kind: pods, deployments, services, ingresses, pvc, configmaps, secrets, jobs, cronjobs, namespaces (singular aliases also accepted)",
        "yaml" => "Complete Kubernetes YAML manifest",
        _ => "Resource name",
    };

    private static string DeployComposeSchema() =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"yaml\":{\"type\":\"string\",\"description\":\"A complete docker-compose YAML defining all services (e.g. wordpress + db). Services resolve each other by service name over DNS.\"}," +
        "\"projectName\":{\"type\":\"string\",\"description\":\"Optional project name; defaults to the compose 'name' or 'ai-compose'.\"}" +
        "},\"required\":[\"yaml\"],\"additionalProperties\":false}";

    private static string BulkContainerSchema(bool includeOnlyRunning)
    {
        var onlyRunning = includeOnlyRunning
            ? "\"onlyRunning\":{\"type\":\"boolean\",\"description\":\"If true (default) only running containers are affected; if false, stopped containers too.\"},"
            : string.Empty;
        return "{\"type\":\"object\",\"properties\":{" +
               onlyRunning +
               "\"scope\":{\"type\":\"string\",\"enum\":[\"all\"],\"description\":\"Explicit all-container intent. Cannot be combined with filters.\"}," +
               "\"namePrefix\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Only affect containers whose name starts with this nonblank value.\"}," +
               "\"nameContains\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Only affect containers whose name contains this nonblank substring.\"}" +
               "},\"required\":[],\"additionalProperties\":false," +
               "\"oneOf\":[{\"required\":[\"scope\"],\"not\":{\"anyOf\":[{\"required\":[\"namePrefix\"]},{\"required\":[\"nameContains\"]}]}}," +
               "{\"not\":{\"required\":[\"scope\"]},\"anyOf\":[{\"required\":[\"namePrefix\"]},{\"required\":[\"nameContains\"]}]}]}";
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
        return await ResolveBulkAsync(call, true, namePrefix, nameContains, ct).ConfigureAwait(false);
    }

    private async Task<AssistantResolvedToolCall> ResolveRemoveAllAsync(AiToolCall call, bool onlyRunning, string? namePrefix, string? nameContains, CancellationToken ct)
    {
        return await ResolveBulkAsync(call, onlyRunning, namePrefix, nameContains, ct).ConfigureAwait(false);
    }

    private async Task<AssistantResolvedToolCall> ResolveBulkAsync(
        AiToolCall call, bool onlyRunning, string? namePrefix, string? nameContains, CancellationToken ct)
    {
        var inventory = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        var targets = inventory.Where(c => !onlyRunning || c.State.IsRunning())
            .Where(c => MatchesNameFilter(c.Name, namePrefix, nameContains))
            .Select(CaptureTarget).ToArray();
        if (targets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != targets.Length)
            throw new InvalidOperationException("Container inventory has ambiguous IDs; no action was prepared.");
        var remove = call.Name == "remove_all_containers";
        return Resolved(call, remove ? AssistantPermissionCategory.Destructive : AssistantPermissionCategory.Lifecycle,
            DescribeBulkScope(remove ? "Remove" : "Stop", onlyRunning ? "running containers" : "containers", namePrefix, nameContains),
            BulkTargetDetails(targets), token => ExecuteTargetsAsync(call.Name, targets, onlyRunning, token));
    }

    private async Task<AssistantResolvedToolCall> ResolveContainerAsync(AiToolCall call, string id, CancellationToken ct)
    {
        var inventory = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        var resolvedId = ContainerIdentity.ResolveId(inventory.Select(c => c.Id), id);
        var matches = inventory.Where(c => string.Equals(c.Id, resolvedId, StringComparison.Ordinal) ||
            string.Equals(c.Name, id, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Container target is missing or ambiguous; no action was prepared.");
        var target = CaptureTarget(matches[0]);
        return Resolved(call, call.Name == "remove_container" ? AssistantPermissionCategory.Destructive : AssistantPermissionCategory.Lifecycle,
            $"{call.Name}: {target.Name} ({target.Id})", BulkTargetDetails([target]),
            token => ExecuteTargetsAsync(call.Name, [target], call.Name == "stop_container", token));
    }

    // Copy values, never retain mutable inventory rows or re-run name filters after approval.
    private sealed record ContainerTarget(string Id, string Name, string Image, long CreatedAt, bool CreatedAtKnown);
    private sealed record TargetOutcome(string Id, string Name, string Status, string Detail);

    private static ContainerTarget CaptureTarget(ContainerInfo container)
    {
        if (string.IsNullOrWhiteSpace(container.Id))
            throw new InvalidOperationException("Container has no stable ID; no action was prepared.");
        return new(container.Id, container.Name, container.Image, container.CreatedAt, container.CreatedAtKnown);
    }

    private async Task<string> ExecuteTargetsAsync(string tool, ContainerTarget[] targets, bool onlyRunning, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var outcomes = new List<TargetOutcome>();
        var cancelled = false;
        for (var index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            var mutationStarted = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                var inventory = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var matches = inventory.Where(c => string.Equals(c.Id, target.Id, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1 || CaptureTarget(matches[0]) != target ||
                    (onlyRunning && !matches[0].State.IsRunning()))
                {
                    outcomes.Add(new(target.Id, target.Name, "skipped", "Target missing, ambiguous, identity changed, or no longer running; fresh approval required."));
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                mutationStarted = true;
                var result = tool switch
                {
                    "start_container" => await wslc.StartContainerAsync(target.Id, ct).ConfigureAwait(false),
                    "restart_container" => await wslc.RestartContainerAsync(target.Id, ct).ConfigureAwait(false),
                    "stop_container" or "stop_all_containers" => await wslc.StopContainerAsync(target.Id, ct).ConfigureAwait(false),
                    "remove_container" or "remove_all_containers" => await wslc.RemoveContainerAsync(target.Id, force: true, ct).ConfigureAwait(false),
                    _ => throw new InvalidOperationException("Unsupported container mutation."),
                };
                outcomes.Add(new(target.Id, target.Name, result.Success ? "succeeded" : "failed", Summarize(result)));
            }
            catch (OperationCanceledException)
            {
                if (outcomes.Count == 0 && !mutationStarted)
                    throw;
                cancelled = true;
                outcomes.Add(new(target.Id, target.Name, mutationStarted ? "unknown" : "not_run",
                    mutationStarted ? "Cancelled during mutation; outcome unknown. Inspect before any retry." : "Cancelled before mutation."));
                for (var remaining = index + 1; remaining < targets.Length; remaining++)
                    outcomes.Add(new(targets[remaining].Id, targets[remaining].Name, "not_run", "Cancelled; not attempted."));
                break;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or
                System.ComponentModel.Win32Exception or JsonException or TimeoutException)
            {
                // Preserve completed outcomes and stop: an exception is not evidence that a mutation failed atomically.
                outcomes.Add(new(target.Id, target.Name, mutationStarted ? "unknown" : "not_run",
                    $"Execution stopped: {ex.Message}. No automatic retry."));
                for (var remaining = index + 1; remaining < targets.Length; remaining++)
                    outcomes.Add(new(targets[remaining].Id, targets[remaining].Name, "not_run", "Stopped after an execution or inventory error."));
                break;
            }
        }
        var status = cancelled ? "cancelled" : outcomes.Count == 0 ? "no_targets" :
            outcomes.All(o => o.Status == "succeeded") ? "succeeded" :
            outcomes.Any(o => o.Status == "succeeded") ? "partial" : "failed";
        return AiTextSanitizer.Sanitize(JsonSerializer.Serialize(new { status, outcomes }, JsonOptions));
    }

    private static string DescribeBulkScope(string verb, string scope, string? namePrefix, string? nameContains)
    {
        if (!string.IsNullOrWhiteSpace(namePrefix))
        {
            return $"{verb} {scope} named \"{namePrefix.Trim()}*\"" +
                (nameContains is null ? "" : $" containing \"{nameContains.Trim()}\"");
        }

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            return $"{verb} {scope} containing \"{nameContains.Trim()}\"";
        }

        return $"{verb} all {scope}";
    }

    private static string BulkTargetDetails(IReadOnlyList<ContainerTarget> targets) =>
        targets.Count == 0
            ? "No matching containers."
            : "Targets:\n" + string.Join(Environment.NewLine, targets.Select(t => $"{t.Name} (ID: {t.Id}, image: {t.Image}, created: {(t.CreatedAtKnown ? t.CreatedAt.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown")})"));

    private static AssistantResolvedToolCall Resolved(
        AiToolCall call,
        AssistantPermissionCategory category,
        string summary,
        string details,
        Func<CancellationToken, Task<string>> execute) =>
        new(call, category, AiTextSanitizer.Sanitize(summary), AiTextSanitizer.Sanitize(details),
            async token => AiTextSanitizer.Sanitize(await execute(token).ConfigureAwait(false)));

    private static string ArgumentSchema(string tool) => tool switch
    {
        "list_containers" or "list_images" or "list_volumes" or "list_networks" or "engine_status" or
            "list_compose_projects" or "k8s_status" or "cluster_start" or "cluster_stop" =>
            """{"type":"object","properties":{},"additionalProperties":false}""",
        "inspect_container" or "start_container" or "stop_container" or "restart_container" or "remove_container" =>
            ObjectSchema(("id", "string", "")),
        "get_container_logs" => ObjectSchema(("id", "string", ""), ("tail", "integer", "")),
        "pull_image" => ObjectSchema(("reference", "string", "")),
        "deploy_template" => ObjectSchema(("idOrName", "string", "")),
        "create_volume" or "remove_volume" or "create_network" or "remove_network" => ObjectSchema(("name", "string", "")),
        "list_registry_repositories" => ObjectSchema(("registry", "string", "")),
        "list_registry_tags" => ObjectSchema(("registry", "string", ""), ("repository", "string", "")),
        "list_k8s_resources" => ObjectSchema(("kind", "string", ""), ("namespace", "string", "")),
        "get_k8s_logs" => ObjectSchema(("namespace", "string", ""), ("name", "string", ""), ("tail", "integer", "")),
        "apply_yaml" => ObjectSchema(("yaml", "string", "")),
        "scale_deployment" => ObjectSchema(("namespace", "string", ""), ("name", "string", ""), ("replicas", "integer", "")),
        "restart_deployment" => ObjectSchema(("namespace", "string", ""), ("name", "string", "")),
        "delete_resource" => ObjectSchema(("kind", "string", ""), ("namespace", "string", ""), ("name", "string", "")),
        "run_container" => RunContainerSchema(),
        "deploy_compose" => DeployComposeSchema(),
        "stop_all_containers" => BulkContainerSchema(false),
        "remove_all_containers" => BulkContainerSchema(true),
        _ => throw new InvalidOperationException($"Tool '{tool}' is not allowed."),
    };

    private static JsonElement ValidateArgs(AiToolCall call)
    {
        using var schemaDocument = JsonDocument.Parse(ArgumentSchema(call.Name));
        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            args = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Invalid tool arguments: a valid JSON object is required.", ex);
        }

        if (args.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Invalid tool arguments: a JSON object is required.");
        var schema = schemaDocument.RootElement;
        var properties = schema.GetProperty("properties");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in args.EnumerateObject())
        {
            if (!seen.Add(field.Name) || !properties.TryGetProperty(field.Name, out var property))
                throw new InvalidOperationException($"Invalid tool arguments: unsupported or duplicate field '{field.Name}'.");
            var valid = property.GetProperty("type").GetString() switch
            {
                "string" => field.Value.ValueKind == JsonValueKind.String &&
                    (!string.IsNullOrWhiteSpace(field.Value.GetString()) ||
                        (call.Name == "delete_resource" && field.Name == "namespace" && field.Value.GetString() == "")),
                "boolean" => field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out var number) &&
                    (!property.TryGetProperty("minimum", out var min) || number >= min.GetInt32()) &&
                    (!property.TryGetProperty("maximum", out var max) || number <= max.GetInt32()),
                "array" => field.Value.ValueKind == JsonValueKind.Array &&
                    field.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())),
                _ => false,
            };
            if (!valid)
                throw new InvalidOperationException($"Invalid tool arguments: field '{field.Name}' has an invalid type, empty value, or out-of-range value.");
        }
        if (schema.TryGetProperty("required", out var required))
            foreach (var field in required.EnumerateArray())
                if (!seen.Contains(field.GetString()!))
                    throw new InvalidOperationException($"Invalid tool arguments: required field '{field.GetString()}' is missing.");

        if (call.Name is "stop_all_containers" or "remove_all_containers")
        {
            var hasScope = seen.Contains("scope");
            var hasFilter = seen.Contains("namePrefix") || seen.Contains("nameContains");
            if (hasScope == hasFilter || (hasScope && args.GetProperty("scope").GetString() != "all"))
                throw new InvalidOperationException("Invalid bulk scope: specify nonblank name filters OR scope=\"all\", never both.");
        }
        if (seen.Contains("kind") && !SupportedResourceKinds.Contains(StringArg(args, "kind").ToLowerInvariant()))
            throw new InvalidOperationException("Invalid tool arguments: unsupported Kubernetes resource kind.");
        if (call.Name == "run_container")
            ValidateRunArguments(args);
        return args;
    }

    private static readonly HashSet<string> SupportedResourceKinds = new(StringComparer.Ordinal)
    {
        "pod", "pods", "deployment", "deployments", "service", "services", "ingress", "ingresses",
        "pvc", "pvcs", "persistentvolumeclaims", "configmap", "configmaps", "secret", "secrets",
        "job", "jobs", "cronjob", "cronjobs", "namespace", "namespaces",
    };

    private static void ValidateRunArguments(JsonElement args)
    {
        foreach (var name in new[] { "environment", "labels" })
        {
            if (!args.TryGetProperty(name, out var items))
                continue;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                var text = item.GetString()!;
                var separator = text.IndexOf('=');
                if (separator <= 0 || string.IsNullOrWhiteSpace(text[..separator]) || !keys.Add(text[..separator].Trim()))
                    throw new InvalidOperationException($"Invalid tool arguments: '{name}' requires unique nonblank KEY=VALUE entries.");
            }
        }
        if (args.TryGetProperty("cpuLimit", out var cpu) &&
            (!decimal.TryParse(cpu.GetString(), System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0))
            throw new InvalidOperationException("Invalid tool arguments: cpuLimit must be a positive decimal.");
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
