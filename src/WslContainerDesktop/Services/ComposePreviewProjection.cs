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

using System.Text.RegularExpressions;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Compose-specific context scrubbing around the shared sanitizer, not a second planner.</summary>
public sealed class ComposePreviewProjection
{
    private readonly string[] _privateValues;

    public ComposePreviewProjection(ComposeProject project)
    {
        // Even innocently named variables can hold secrets. Mask their values everywhere, including
        // names, image references, diagnostics and logical source breadcrumbs, not only env rows.
        _privateValues = project.Services.Concat(project.AppliedServices.Values.Select(a => a.Service))
            .SelectMany(s => s.Options.EnvironmentVariables.Concat(s.Build?.Args ?? [])
                .Select(v => v.Contains('=') ? v[(v.IndexOf('=') + 1)..] : "")
                .Concat(s.Options.Labels.Where(p => p.Key != ComposeProject.ProjectLabel &&
                    p.Key != ComposeProject.ServiceLabel && p.Key != ComposeProject.InstanceLabel).Select(p => p.Value))
                .Concat(s.Build?.Labels.Values ?? Enumerable.Empty<string>())
                .Concat(s.Options.Health?.Test ?? [])
                .Concat(s.Health?.DesiredHealth?.Test ?? [])
                .Concat(new[] { s.Options.User ?? "", s.Options.Command ?? "", s.Options.Entrypoint ?? "",
                    s.Health?.Command ?? "" }))
            .Concat(project.Secrets.Concat(project.Configs).Select(s => s.File ?? ""))
            .Concat(project.Networks.SelectMany(n => n.Labels.Values))
            .Concat(project.Volumes.SelectMany(v => v.Labels.Values))
            .Concat(project.Networks.SelectMany(n => n.DriverOpts).Concat(project.Volumes.SelectMany(v => v.DriverOpts))
                .Select(v => v.Contains('=') ? v[(v.IndexOf('=') + 1)..] : v))
            .Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length).ToArray();
    }

    public string Redact(string? value)
    {
        var text = value ?? "";
        foreach (var secret in _privateValues)
            text = text.Replace(secret, "<redacted>", StringComparison.Ordinal);
        // User-info can occur in image/registry references without a URI scheme.
        text = Regex.Replace(text, @"[^\s/]+:[^\s/@]+@", "<credentials>@");
        text = Regex.Replace(text, @"(?i)([a-z]:[\\/]Users[\\/])[^\\/\s]+", "$1<user>");
        return AiTextSanitizer.Redact(text);
    }

    public ComposeCompatibilityPreview Create(ComposeProject project, ComposeOperationRequest request,
        ComposeReconciliationPlan plan, IReadOnlyList<ComposeCompatibilitySetting>? additional = null)
    {
        var rows = new List<ComposeCompatibilitySetting>();
        void Add(string service, string key, string value, string explanation,
            ComposeSettingDisposition disposition = ComposeSettingDisposition.Supported,
            WslcCapabilitySupport? capability = null, string? source = null) =>
            rows.Add(new(Redact(service), key, disposition, Redact(value), Redact(explanation),
                Redact(source ?? $"Resolved model: services.{service}.{key}"), capability));

        foreach (var group in plan.Services.GroupBy(p => p.Service.Name))
        {
            var entry = group.First();
            var service = entry.Service;
            var options = service.Options;
            var needsStartupSettings = request.Operation is ComposeLifecycleOperation.Up or ComposeLifecycleOperation.Restart &&
                group.Any(p => p.Action != ComposeServiceAction.Remove && !(p.Action == ComposeServiceAction.Keep && p.ContainerId is null));
            Add(service.Name, "replicas", entry.DesiredReplicas.ToString(),
                "Request override > saved operator override > imported scale/deploy.replicas > one. All dependency instances must be ready.");
            // Warnings about losing a container are only true when one is actually being replaced.
            // Stating them on a first-time create makes a routine launch look destructive.
            var blockedInstances = group.Any(p => p.Action == ComposeServiceAction.Blocked);
            var replacing = group.Any(p => p.Action is ComposeServiceAction.Recreate or ComposeServiceAction.Remove);
            var creating = group.Any(p => p.Action == ComposeServiceAction.Create);
            Add(service.Name, "instances", string.Join("\n", group.Select(p =>
                $"{p.InstanceKey} · {p.ContainerName} · {p.Action} · {Redact(p.Reason)}")),
                replacing
                    ? "Replacing a container discards anything written inside it. Mounted volumes are kept. There is no atomic rollback across the project."
                    : creating
                        ? "New containers are created. Nothing existing is replaced."
                        : "Existing containers are reused as they are.",
                blockedInstances ? ComposeSettingDisposition.Blocked :
                replacing ? ComposeSettingDisposition.Approximated : ComposeSettingDisposition.Supported);
            Add(service.Name, "image", options.Image,
                entry.ImageAction switch
                {
                    // A download is ordinary. It only carries a consequence when an existing
                    // container has to be replaced to pick up the newly resolved image.
                    ComposeImageAction.Pull when replacing =>
                        "Downloads this image. Because a tag can move, the existing container is replaced to use it.",
                    ComposeImageAction.Pull => "Downloads this image; it is not on this machine yet.",
                    ComposeImageAction.Build => "Builds this image from its build context.",
                    _ => "Already on this machine; nothing is downloaded.",
                },
                entry.ImageAction == ComposeImageAction.Pull && replacing
                    ? ComposeSettingDisposition.Approximated : ComposeSettingDisposition.Supported);
            Add(service.Name, "depends_on", string.Join("\n", ComposeReconciliationPlanner.Dependencies(service)
                .Select(d => $"{d.ServiceName}: {d.Condition}, required={d.Required}, restart={d.Restart}")),
                "Selected active dependency closure; readiness/completion applies to every replica.");
            Add(service.Name, "profiles", string.Join(", ", service.Profiles), "Only active or explicitly selected logical services participate.");
            Add(service.Name, "ports", string.Join("\n", options.PortMappings), request.Operation == ComposeLifecycleOperation.Up
                ? "Published host bindings are checked against selected services and observed inventory. Host processes outside WSLC are not inspected."
                : "Existing applied bindings; this operation does not change published ports.");
            Add(service.Name, "volumes", string.Join("\n", options.Volumes),
                entry.StorageWarning ?? (replacing
                    ? "Named and anonymous volumes are kept when the container is replaced; bind-mount contents are not copied."
                    : "Storage is mounted as declared."),
                entry.StorageWarning is null ? ComposeSettingDisposition.Supported : ComposeSettingDisposition.Approximated);
            List<NetworkAttachment> endpoints;
            try { endpoints = options.GetNetworkAttachments(); }
            catch (InvalidOperationException)
            {
                endpoints = [];
                Add(service.Name, "networks", "Invalid endpoint declaration.",
                    "Resolve conflicting endpoint settings and review again.", ComposeSettingDisposition.Blocked);
            }
            Add(service.Name, "backend", request.Operation == ComposeLifecycleOperation.Up
                    ? entry.Backend.ToString() : "Existing container only",
                request.Operation == ComposeLifecycleOperation.Up
                    ? "Native multi-network: create, connect all endpoints, start. Legacy: run on the primary network. Failed native operations never fall back."
                    : "Uses applied configuration, not pending desired edits. No create, build or image acquisition.",
                entry.Backend == ComposeExecutionBackend.Unknown && needsStartupSettings ? ComposeSettingDisposition.Blocked :
                entry.NetworkSupport == WslcCapabilitySupport.Unsupported ? ComposeSettingDisposition.Approximated : ComposeSettingDisposition.Supported,
                entry.NetworkSupport);
            for (var i = 0; i < endpoints.Count; i++)
            {
                var endpoint = endpoints[i];
                var ignored = i > 0 && entry.Backend == ComposeExecutionBackend.LegacyRun &&
                    request.Operation == ComposeLifecycleOperation.Up;
                Add(service.Name, $"networks[{i + 1}]", $"{endpoint.Network}; aliases: {string.Join(", ", endpoint.Aliases)}; IPv4: {endpoint.Ipv4Address}",
                    ignored ? "Legacy primary-only fallback: this endpoint, its aliases and address are NOT applied. Desired settings remain saved." :
                        request.Operation == ComposeLifecycleOperation.Up
                            ? "Endpoint attached before startup; service and instance DNS aliases are supplied by the shared planner."
                            : "Applied endpoint declaration; this operation does not reconfigure endpoints.",
                    ignored ? ComposeSettingDisposition.Ignored : ComposeSettingDisposition.Supported, entry.NetworkSupport);
            }
            if (options.HasSpecialNetworkMode)
                Add(service.Name, "network_mode", options.NetworkMode ?? options.Network ?? "", "Namespace mode replaces ordinary network endpoints.");
            Add(service.Name, "healthcheck", $"Probe owner: {entry.HealthOwner}; auto-heal owner: application",
                entry.HealthOwner == ComposePolicyOwner.Application
                    ? "This app runs the health probe and auto-heal, so both pause while the app is closed. Probe details are withheld."
                    : "Health checks come from the image or engine. Probe details are withheld.",
                entry.HealthOwner == ComposePolicyOwner.Unknown && needsStartupSettings ? ComposeSettingDisposition.Blocked :
                entry.HealthOwner == ComposePolicyOwner.Application ? ComposeSettingDisposition.Approximated : ComposeSettingDisposition.Supported);
            Add(service.Name, "restart", $"{service.Restart}; owner: {(service.Restart == RestartPolicyKind.No ? "none" : "application")}",
                service.Restart == RestartPolicyKind.No
                    ? "No restart policy is requested."
                    : "The engine has no restart-policy flag, so this app restarts the container and pauses while the app is closed. Stopping it yourself is remembered.",
                service.Restart == RestartPolicyKind.No ? ComposeSettingDisposition.Supported : ComposeSettingDisposition.Approximated);
            if (!string.IsNullOrWhiteSpace(entry.CompatibilityWarning))
                Add(service.Name, "compatibility", entry.CompatibilityWarning, "Backend-specific limitations.", ComposeSettingDisposition.Approximated);
            Add(service.Name, "resources", $"CPU: {options.CpuLimit ?? "engine default"}; memory: {options.MemoryLimit ?? "engine default"}; shm: {options.ShmSize ?? "engine default"}; GPU: {options.AllGpus}",
                "Local engine limits, not Swarm reservations, placement or scheduling.");
            Add(service.Name, "environment", $"{options.EnvironmentVariables.Count} entries; all names and values withheld.",
                "Resolved environment is passed to execution, never copied to the preview or audit.");
            Add(service.Name, "process", "Command, entrypoint and user values withheld.",
                $"Command override: {options.Command is not null}; entrypoint override: {options.Entrypoint is not null}; user override: {options.User is not null}. Working directory: {Redact(options.WorkingDir)}.");
            Add(service.Name, "dns / hostname", $"DNS: {string.Join(", ", options.Dns)}; search: {string.Join(", ", options.DnsSearch)}; options: {string.Join(", ", options.DnsOptions)}; hostname: {options.Hostname}; domain: {options.Domainname}",
                "Mapped to engine run/create settings.");
            Add(service.Name, "tmpfs / ulimits / stop", $"tmpfs: {string.Join(", ", options.Tmpfs)}; ulimits: {string.Join(", ", options.Ulimits)}; stop signal: {options.StopSignal}; grace: {service.StopGracePeriodSeconds}",
                "Applied through engine options and stop timeout.");
            if (service.ExtraHosts.Count > 0)
                Add(service.Name, "extra_hosts", string.Join("\n", service.ExtraHosts),
                    "Applied by exec after start, not an engine --add-host option; startup may observe the old hosts file.", ComposeSettingDisposition.Approximated);
            foreach (var kind in new[] { ("secrets", service.Secrets), ("configs", service.Configs) })
                foreach (var mount in kind.Item2)
                    Add(service.Name, kind.Item1, $"{mount.Source} → {mount.Target}; file contents/path withheld",
                        "Read-only staged file bind mount, not an engine secret store. Staging occurs only after confirmation.",
                        ComposeSettingDisposition.Approximated);
            if (service.Build is not null)
                Add(service.Name, "build", $"Work: {entry.ImageAction}; arguments and labels withheld.",
                    "Build inputs are validated before replacement. Context contents are not recursively watched.", ComposeSettingDisposition.Approximated);
        }
        foreach (var warning in request.Operation == ComposeLifecycleOperation.Up ? project.Warnings : [])
            Add("Project", "import diagnostic", warning,
                "Preserved importer diagnostic; source breadcrumb/line is included when available.",
                warning.Contains("is unset; using an empty string", StringComparison.Ordinal) ||
                warning.StartsWith("Blocked deployment:", StringComparison.Ordinal)
                    ? ComposeSettingDisposition.Blocked : ComposeSettingDisposition.Ignored, source: "Importer diagnostic");
        if (additional is not null)
            rows.AddRange(additional.Select(r => r with
            {
                Service = Redact(r.Service), EffectiveValue = Redact(r.EffectiveValue),
                Explanation = Redact(r.Explanation), Source = Redact(r.Source),
            }));
        if (rows.Count == 0)
            Add("Project", "selection", "No active instances.", "No container actions are required.");
        return new(Redact(project.Name), request.Operation, rows.AsReadOnly());
    }
}
