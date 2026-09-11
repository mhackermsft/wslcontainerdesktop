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

namespace WslContainerDesktop.Services;

/// <summary>
/// Resolves how the Open WebUI template should be deployed. Running a second Ollama would waste
/// several GB of models and take the same port, so an existing runtime is reused when one is
/// present and only the web UI is deployed against it.
/// </summary>
public sealed class OpenWebUiPlanner(IWslcService wslc, IWslcCapabilitiesService capabilities)
{
    public const string TemplateId = "open-webui";

    /// <summary>Network the template creates, and the alias the web UI resolves Ollama by.</summary>
    public const string NetworkName = "openwebui-net";
    public const string OllamaAlias = "ollama";
    private const string Image = "ollama/ollama";

    /// <summary>Container the bundled stack creates, used to install a model into it.</summary>
    public const string BundledOllamaContainer = "openwebui_ollama";

    /// <summary>Default suggestion: a small, widely used chat model that is quick to download.</summary>
    public const string RecommendedModel = "llama3.2:3b";

    /// <summary>Suggestions offered alongside free entry. Any Ollama model name is accepted.</summary>
    public static IReadOnlyList<string> SuggestedModels { get; } =
        ["llama3.2:3b", "qwen2.5:7b", "gemma3:4b", "phi4-mini:latest"];

    /// <summary>
    /// Accepts only an ordinary Ollama model reference. The value reaches a shell through
    /// <c>exec … sh -c</c>, so anything that could terminate or extend that command is rejected
    /// rather than escaped.
    /// </summary>
    public static bool IsValidModelName(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Length <= 200 &&
        System.Text.RegularExpressions.Regex.IsMatch(model.Trim(),
            @"^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*(:[A-Za-z0-9][A-Za-z0-9._-]*)?$");

    /// <summary>
    /// Installs a model into the Ollama container this template deployed, so the web UI has
    /// something to talk to. Only ever targets the bundled container, never a reused runtime.
    /// </summary>
    public async Task<CommandResult> InstallModelAsync(string model, CancellationToken ct = default)
    {
        if (!IsValidModelName(model))
            throw new ArgumentException("Unsupported Ollama model name.", nameof(model));
        return await wslc.ExecAsync(BundledOllamaContainer, $"ollama pull {model.Trim()}", ct).ConfigureAwait(false);
    }

    /// <param name="ExistingOllamaId">Immutable ID of the runtime to reuse, or null to deploy one.</param>
    /// <param name="ExistingOllamaName">Display name for messages and confirmation text.</param>
    /// <param name="UsesGpu">Whether a newly deployed runtime requests GPU passthrough.</param>
    public sealed record Plan(string Yaml, string? ExistingOllamaId, string? ExistingOllamaName, bool UsesGpu = false)
    {
        public bool ReusesExistingRuntime => ExistingOllamaId is not null;
    }

    public async Task<Plan> PlanAsync(CancellationToken ct = default)
    {
        var existing = await FindOllamaAsync(ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // A reused runtime keeps whatever access it was created with; this template never
            // recreates someone else's container to change it.
            return new(ReuseYaml, existing.Value.Id, existing.Value.Name);
        }

        // CPU-only inference is dramatically slower, so request GPU passthrough when the engine
        // definitively supports it. Unsupported or unknown support stays on CPU rather than
        // risking a deployment that fails on an unrecognized flag.
        var gpu = await SupportsGpuAsync(ct).ConfigureAwait(false);
        return new(gpu ? BundledGpuYaml : BundledYaml, null, null, gpu);
    }

    private async Task<bool> SupportsGpuAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = await capabilities.GetAsync(ct).ConfigureAwait(false);
            return snapshot[WslcFeature.CreateGpus].Support == WslcCapabilitySupport.Supported;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without evidence of support, deploy for CPU instead of failing the launch.
            return false;
        }
    }

    /// <summary>
    /// Finds a container running the Ollama image, preferring a running one. Never inspects or
    /// mutates it: the template only needs somewhere to point the web UI.
    /// </summary>
    private async Task<(string Id, string Name)?> FindOllamaAsync(CancellationToken ct)
    {
        try
        {
            var containers = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
            if (containers.Count == 0)
                return null;

            // Inventory reports either a reference or a bare image ID, so resolve IDs through the
            // image list. Matching only on "ollama/ollama" would miss a container reported by ID.
            var ollamaImageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var image in await wslc.ListImagesAsync(ct).ConfigureAwait(false))
                {
                    if (IsOllamaReference(image.Repository) && !string.IsNullOrWhiteSpace(image.Id))
                    {
                        ollamaImageIds.Add(image.Id.Trim());
                        if (image.Id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            ollamaImageIds.Add(image.Id["sha256:".Length..]);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall back to reference matching below.
            }

            var matches = containers
                .Where(c => !string.IsNullOrWhiteSpace(c.Id) && IsOllamaContainer(c, ollamaImageIds))
                .OrderByDescending(c => c.State == ContainerState.Running)
                .ToArray();
            if (matches.Length == 0)
                return null;
            var match = matches[0];
            return (match.Id, string.IsNullOrWhiteSpace(match.Name) ? match.Id : match.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If inventory cannot be read, deploy the self-contained stack rather than guessing that
            // a reusable runtime exists.
            return null;
        }
    }

    private static bool IsOllamaContainer(ContainerInfo container, HashSet<string> ollamaImageIds)
    {
        var image = container.Image?.Trim();
        if (string.IsNullOrWhiteSpace(image))
            return false;
        if (IsOllamaReference(image))
            return true;
        // Bare image IDs may be reported short; compare on the shorter of the two.
        foreach (var id in ollamaImageIds)
        {
            var length = Math.Min(id.Length, image.Length);
            if (length >= 12 && string.Equals(id[..length], image[..length], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsOllamaReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return false;
        var value = reference.Trim();
        // Strip any tag or digest so "ollama/ollama:latest" and a registry-qualified name both match.
        var lastSlash = value.LastIndexOf('/');
        var separator = value.IndexOfAny([':', '@'], lastSlash < 0 ? 0 : lastSlash);
        if (separator >= 0)
            value = value[..separator];
        return value.Equals(Image, StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/" + Image, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Attaches the reused runtime to the template's network under a stable alias so the
    /// web UI resolves it by name instead of an address that changes on restart.</summary>
    public Task<CommandResult> AttachExistingAsync(string containerId, CancellationToken ct = default) =>
        wslc.ConnectNetworkAsync(
            new NetworkAttachment { Network = NetworkName, Aliases = { OllamaAlias } }, containerId, ct);

    /// <summary>No Ollama present: deploy one alongside the web UI. The runtime port is deliberately
    /// not published, so this never competes for 11434 with a runtime added later.</summary>
    internal const string BundledYaml = """
        services:
          ollama:
            image: ollama/ollama:latest
            volumes:
              - openwebui-ollama:/root/.ollama
            networks:
              webui:
                aliases:
                  - ollama
          open-webui:
            image: ghcr.io/open-webui/open-webui:main
            depends_on:
              - ollama
            ports:
              - "8084:8080"
            environment:
              OLLAMA_BASE_URL: http://ollama:11434
            volumes:
              - openwebui-data:/app/backend/data
            networks:
              - webui
        volumes:
          openwebui-ollama:
          openwebui-data:
        networks:
          webui:
            name: openwebui-net
        """;

    /// <summary>
    /// The bundled stack with GPU passthrough requested for the runtime. Selected only when the
    /// engine advertises GPU creation; requesting all GPUs is not by itself proof of acceleration.
    /// </summary>
    internal const string BundledGpuYaml = """
        services:
          ollama:
            image: ollama/ollama:latest
            volumes:
              - openwebui-ollama:/root/.ollama
            networks:
              webui:
                aliases:
                  - ollama
            deploy:
              resources:
                reservations:
                  devices:
                    - capabilities: [gpu]
          open-webui:
            image: ghcr.io/open-webui/open-webui:main
            depends_on:
              - ollama
            ports:
              - "8084:8080"
            environment:
              OLLAMA_BASE_URL: http://ollama:11434
            volumes:
              - openwebui-data:/app/backend/data
            networks:
              - webui
        volumes:
          openwebui-ollama:
          openwebui-data:
        networks:
          webui:
            name: openwebui-net
        """;

    /// <summary>The GPU reservation inserted into <see cref="BundledGpuYaml"/>; kept here so a
    /// test can prove the two stacks differ by nothing else.</summary>
    internal const string GpuReservationBlock = """
            deploy:
              resources:
                reservations:
                  devices:
                    - capabilities: [gpu]
        """;

    /// <summary>An Ollama runtime already exists: deploy only the web UI and point it at that
    /// runtime, which is attached to this network after the project starts.</summary>
    internal const string ReuseYaml = """
        services:
          open-webui:
            image: ghcr.io/open-webui/open-webui:main
            ports:
              - "8084:8080"
            environment:
              OLLAMA_BASE_URL: http://ollama:11434
            volumes:
              - openwebui-data:/app/backend/data
            networks:
              - webui
        volumes:
          openwebui-data:
        networks:
          webui:
            name: openwebui-net
        """;
}
