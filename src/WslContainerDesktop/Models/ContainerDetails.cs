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

using System.Text.Json;

namespace WslContainerDesktop.Models;

/// <summary>
/// Selected, human-friendly fields parsed from `wslc inspect --type container`.
/// </summary>
public sealed class ContainerDetails
{
    /// <summary>Gets or sets the command.</summary>
    public string Command { get; init; } = "-";
    /// <summary>Gets or sets the ip address.</summary>
    public string IpAddress { get; init; } = "-";
    /// <summary>Gets or sets the started at.</summary>
    public string StartedAt { get; init; } = "-";
    /// <summary>Gets or sets the working dir.</summary>
    public string WorkingDir { get; init; } = "-";
    /// <summary>Gets or sets the network mode.</summary>
    public string NetworkMode { get; init; } = "-";
    /// <summary>Gets or sets the environment.</summary>
    public IReadOnlyList<string> Environment { get; init; } = Array.Empty<string>();
    /// <summary>Gets or sets the mounts.</summary>
    public IReadOnlyList<string> Mounts { get; init; } = Array.Empty<string>();
    /// <summary>Gets or sets the networks.</summary>
    public IReadOnlyList<NetworkAttachment> Networks { get; init; } = Array.Empty<NetworkAttachment>();
    /// <summary>Gets or sets the size rw bytes.</summary>
    public long? SizeRwBytes { get; init; }
    /// <summary>Gets or sets the size root fs bytes.</summary>
    public long? SizeRootFsBytes { get; init; }
    /// <summary>Gets the size rw display.</summary>
    public string SizeRwDisplay => SizeRwBytes is long bytes ? HumanSize(bytes) : "-";
    /// <summary>Gets the size root fs display.</summary>
    public string SizeRootFsDisplay => SizeRootFsBytes is long bytes ? HumanSize(bytes) : "-";

    /// <summary>Parses input into parse data used by the app.</summary>
    /// <param name="json">The json value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public static ContainerDetails Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var el = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0]
                : root;

            var command = "-";
            var workingDir = "-";
            var env = new List<string>();

            if (el.TryGetProperty("Config", out var config))
            {
                var parts = new List<string>();
                if (config.TryGetProperty("Entrypoint", out var ep) && ep.ValueKind == JsonValueKind.Array)
                {
                    parts.AddRange(ep.EnumerateArray().Select(x => x.GetString() ?? string.Empty));
                }

                if (config.TryGetProperty("Cmd", out var cmd) && cmd.ValueKind == JsonValueKind.Array)
                {
                    parts.AddRange(cmd.EnumerateArray().Select(x => x.GetString() ?? string.Empty));
                }

                if (parts.Count > 0)
                {
                    command = string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));
                }

                if (config.TryGetProperty("WorkingDir", out var wd) && wd.ValueKind == JsonValueKind.String)
                {
                    workingDir = string.IsNullOrEmpty(wd.GetString()) ? "-" : wd.GetString()!;
                }

                if (config.TryGetProperty("Env", out var envEl) && envEl.ValueKind == JsonValueKind.Array)
                {
                    env.AddRange(envEl.EnumerateArray().Select(x => x.GetString() ?? string.Empty)
                        .Where(s => !string.IsNullOrEmpty(s)));
                }
            }

            var startedAt = "-";
            if (el.TryGetProperty("State", out var state) &&
                state.TryGetProperty("StartedAt", out var sa) &&
                sa.ValueKind == JsonValueKind.String)
            {
                var raw = sa.GetString();
                if (!string.IsNullOrEmpty(raw) && DateTimeOffset.TryParse(raw, out var dto) && dto.Year > 1)
                {
                    startedAt = dto.ToLocalTime().ToString("g");
                }
            }

            var ip = "-";
            var networkMode = "-";
            var networkAttachments = new List<NetworkAttachment>();
            if (el.TryGetProperty("NetworkSettings", out var ns) &&
                ns.TryGetProperty("Networks", out var nets) &&
                nets.ValueKind == JsonValueKind.Object)
            {
                var networkNames = new List<string>();
                var addresses = new List<string>();
                foreach (var net in nets.EnumerateObject())
                {
                    networkNames.Add(net.Name);
                    var attachment = new NetworkAttachment { Network = net.Name };
                    if (net.Value.TryGetProperty("Aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                    {
                        attachment.Aliases = aliases.EnumerateArray()
                            .Where(a => a.ValueKind == JsonValueKind.String)
                            .Select(a => a.GetString()!)
                            .Where(a => !string.IsNullOrWhiteSpace(a))
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                    }

                    if (net.Value.TryGetProperty("IPAMConfig", out var ipam) && ipam.ValueKind == JsonValueKind.Object &&
                        ipam.TryGetProperty("IPv4Address", out var requested) && requested.ValueKind == JsonValueKind.String)
                    {
                        attachment.Ipv4Address = requested.GetString();
                    }

                    if (net.Value.TryGetProperty("IPAddress", out var ipEl) &&
                        ipEl.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(ipEl.GetString()))
                    {
                        attachment.Ipv4Address = ipEl.GetString();
                        addresses.Add($"{net.Name}: {ipEl.GetString()}");
                    }

                    networkAttachments.Add(attachment);
                }

                networkMode = networkNames.Count == 0 ? "-" : string.Join(", ", networkNames);
                ip = addresses.Count == 0 ? "-" : string.Join(", ", addresses);
            }

            if (networkMode == "-" &&
                el.TryGetProperty("HostConfig", out var hc) &&
                hc.TryGetProperty("NetworkMode", out var nm) &&
                nm.ValueKind == JsonValueKind.String)
            {
                networkMode = nm.GetString() ?? "-";
            }

            var mounts = ContainerMounts.Parse(el).Items
                .Where(m => !string.IsNullOrEmpty(m.Destination))
                .Select(m => string.IsNullOrEmpty(m.Source ?? m.Name)
                    ? m.Destination! : $"{m.Source ?? m.Name} -> {m.Destination}")
                .ToList();

            var sizeRw = ReadOptionalInt64(el, "SizeRw");
            var sizeRootFs = ReadOptionalInt64(el, "SizeRootFs");

            return new ContainerDetails
            {
                Command = command,
                WorkingDir = workingDir,
                Environment = env,
                StartedAt = startedAt,
                IpAddress = ip,
                NetworkMode = networkMode,
                Mounts = mounts,
                Networks = networkAttachments,
                SizeRwBytes = sizeRw,
                SizeRootFsBytes = sizeRootFs,
            };
        }
        catch
        {
            return new ContainerDetails();
        }
    }

    private static long? ReadOptionalInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => null,
        };
    }

    private static string HumanSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
