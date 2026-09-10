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
using System.Text.Json.Nodes;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Tests.Services;

internal static class ComposeConformanceProjection
{
    public static JsonObject FromApp(ComposeProject project)
    {
        var services = new JsonObject();
        foreach (var service in project.Services)
        {
            var options = service.Options;
            services.Add(service.Name, new JsonObject
            {
                ["image"] = options.Image,
                ["command"] = options.Command,
                ["environment"] = JsonSerializer.SerializeToNode(options.EnvironmentVariables
                    .Select(v => v.Split('=', 2)).ToDictionary(v => v[0], v => v.Length == 2 ? v[1] : null)),
                ["labels"] = JsonSerializer.SerializeToNode(options.Labels),
                ["ports"] = JsonSerializer.SerializeToNode(options.PortMappings),
                ["volumes"] = JsonSerializer.SerializeToNode(options.Volumes),
                ["dns"] = JsonSerializer.SerializeToNode(options.Dns),
                ["profiles"] = JsonSerializer.SerializeToNode(service.Profiles),
                ["networks"] = JsonSerializer.SerializeToNode(options.NetworkAttachments.ToDictionary(
                    n => n.Network, n => new { aliases = n.Aliases, ipv4_address = n.Ipv4Address })),
                ["depends_on"] = JsonSerializer.SerializeToNode(service.DependsOn.ToDictionary(
                    d => d.ServiceName, d => d.Condition switch
                    {
                        DependencyCondition.ServiceHealthy => "service_healthy",
                        DependencyCondition.ServiceCompletedSuccessfully => "service_completed_successfully",
                        _ => "service_started",
                    })),
                ["healthcheck"] = options.Health is { } health ? new JsonObject
                {
                    ["test"] = JsonSerializer.SerializeToNode(health.Test),
                    ["interval"] = health.Interval,
                    ["timeout"] = health.Timeout,
                    ["start_period"] = health.StartPeriod,
                    ["start_interval"] = health.StartInterval,
                    ["retries"] = health.Retries,
                    ["disable"] = health.IsDisabled,
                } : null,
            });
        }

        return new JsonObject
        {
            ["serviceNames"] = JsonSerializer.SerializeToNode(project.Services.Select(s => s.Name).Order(StringComparer.Ordinal)),
            ["services"] = services,
            ["warnings"] = JsonSerializer.SerializeToNode(project.Warnings),
        };
    }

    // Config JSON expands ports/mounts and argv; the app stores compact strings.
    // Only this documented projection is comparable, not the entire Compose application model.
    public static JsonObject FromReference(JsonObject config)
    {
        var result = (JsonObject)config.DeepClone();
        var services = result["services"]!.AsObject();
        result["serviceNames"] = JsonSerializer.SerializeToNode(services.Select(s => s.Key).Order(StringComparer.Ordinal));
        foreach (var (_, node) in services)
        {
            var service = node!.AsObject();
            // Compose config re-escapes literal dollars for a reloadable document. Compare
            // container environment values, not that serialization escape (never expand $VAR).
            if (service["environment"] is JsonObject environment)
                foreach (var key in environment.Select(e => e.Key).ToArray())
                    if (environment[key] is JsonValue value && value.TryGetValue<string>(out var text))
                        environment[key] = text.Replace("$$", "$", StringComparison.Ordinal);
            if (service["ports"] is JsonArray ports)
                service["ports"] = JsonSerializer.SerializeToNode(ports.Select(p =>
                    (p!["host_ip"] is { } ip ? ip.GetValue<string>() + ":" : "") +
                    (p["published"] is { } published ? published.ToString() + ":" : "") +
                    p["target"] + (p["protocol"]?.GetValue<string>() is { } protocol && protocol != "tcp" ? "/" + protocol : "")));
            if (service["volumes"] is JsonArray volumes)
                service["volumes"] = JsonSerializer.SerializeToNode(volumes.Select(v =>
                    (v!["source"] is { } source ? source.GetValue<string>() + ":" : "") +
                    v["target"] + (v["read_only"]?.GetValue<bool>() == true ? ":ro" : "")));
            if (service["depends_on"] is JsonObject dependencies)
                service["depends_on"] = new JsonObject(dependencies.Select(d =>
                    KeyValuePair.Create(d.Key, d.Value!["condition"]?.DeepClone())));
            if (service["command"] is JsonArray command)
                service["command"] = string.Join(' ', command.Select(c =>
                {
                    var token = c!.GetValue<string>();
                    return token.Any(char.IsWhiteSpace) ? "\"" + token.Replace("\"", "\\\"") + "\"" : token;
                }));
        }
        return result;
    }

    public static JsonNode? At(JsonNode root, string pointer)
    {
        JsonNode? node = root;
        foreach (var part in pointer.TrimStart('/').Split('/'))
        {
            var key = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(key, out node))
                throw new InvalidDataException($"Projection does not contain '{pointer}' (missing '{key}').");
        }
        return node;
    }
}
