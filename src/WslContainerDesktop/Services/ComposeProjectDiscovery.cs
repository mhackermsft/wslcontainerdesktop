// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker

using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Builds Compose rows for externally created projects.</summary>
public static class ComposeProjectDiscovery
{
    private sealed record InspectedContainer(ContainerInfo Container, Dictionary<string, string> Labels);

    /// <summary>Inspects containers and groups those carrying the shared Compose labels.</summary>
    public static async Task<IReadOnlyList<ComposeProject>> DiscoverAsync(
        IReadOnlyList<ContainerInfo> containers,
        Func<string, CancellationToken, Task<CommandResult>> inspect,
        CancellationToken ct = default)
    {
        var inspected = await Task.WhenAll(containers.Select(async container =>
        {
            try
            {
                var result = await inspect(container.Id, ct).ConfigureAwait(false);
                var labels = result.Success ? ReadLabels(result.StandardOutput) : null;
                return labels is null ? null : new InspectedContainer(container, labels);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        })).ConfigureAwait(false);

        return inspected.Where(labels => labels is not null)
            .Cast<InspectedContainer>()
            .Where(entry => entry.Labels.ContainsKey(ComposeProject.ProjectLabel))
            .Where(entry => !entry.Labels.TryGetValue(ComposeProject.ManagedByLabel, out var owner) ||
                !string.Equals(owner, ComposeProject.ManagedByValue, StringComparison.Ordinal))
            .GroupBy(entry => entry.Labels[ComposeProject.ProjectLabel], StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => CreateProject(group.Key, group))
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ComposeProject CreateProject(string name, IEnumerable<InspectedContainer> entries)
    {
        var containers = entries.ToList();
        var labels = containers.Select(entry => entry.Labels).ToList();
        var composeFiles = labels.SelectMany(ReadComposeFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var workingDirectory = labels.Select(label => label.TryGetValue(ComposeProject.WorkingDirectoryLabel, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var project = new ComposeProject
        {
            Name = name,
            IsExternallyManaged = true,
            ExternalContainerCount = containers.Count,
            ExternalRunningCount = containers.Count(entry => entry.Container.State == ContainerState.Running),
            ComposeFiles = composeFiles,
            WorkingDirectory = workingDirectory,
            EnvironmentFile = labels.Select(label => label.TryGetValue(ComposeProject.EnvironmentFileLabel, out var value) ? value : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            Services = labels
                .Where(label => label.TryGetValue(ComposeProject.ServiceLabel, out var service) && !string.IsNullOrWhiteSpace(service))
                .Select(label => label[ComposeProject.ServiceLabel])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(service => service, StringComparer.OrdinalIgnoreCase)
                .Select(service => new ComposeService { Name = service })
                .ToList(),
        };
        return TryReimport(project, composeFiles, workingDirectory) ?? project;
    }

    /// <summary>Loads the ordered local Compose source set before allowing lifecycle actions.</summary>
    private static ComposeProject? TryReimport(ComposeProject discovered, IReadOnlyList<string> files, string? workingDirectory)
    {
        if (files.Count == 0 || files.Any(file => !File.Exists(file))) return null;
        try
        {
            var project = ComposeImporter.ParseProjectFiles(files);
            if (project.Services.Count == 0) return null;
            project.Name = discovered.Name;
            project.ComposeFiles = files.Select(Path.GetFullPath).ToList();
            project.WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(project.ComposeFiles[0]);
            project.EnvironmentFile = discovered.EnvironmentFile;
            project.ExternalContainerCount = discovered.ExternalContainerCount;
            project.ExternalRunningCount = discovered.ExternalRunningCount;
            project.IsExternallyManaged = false;
            project.ApplyProjectNamespacing();
            return project;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ComposeConfigurationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadComposeFiles(Dictionary<string, string> labels)
    {
        if (!labels.TryGetValue(ComposeProject.ConfigFilesLabel, out var value)) return [];
        List<string>? files;
        try
        {
            files = JsonSerializer.Deserialize<List<string>>(value);
        }
        catch (JsonException) { return []; }
        return files?.Where(file => !string.IsNullOrWhiteSpace(file)).ToList() ?? [];
    }

    private static Dictionary<string, string>? ReadLabels(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1) root = root[0];
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object)
                root = config;
            if (!root.TryGetProperty("Labels", out var values) || values.ValueKind != JsonValueKind.Object) return null;
            return values.EnumerateObject()
                .Where(label => label.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(label => label.Name, label => label.Value.GetString()!, StringComparer.Ordinal);
        }
        catch (JsonException) { return null; }
    }
}
