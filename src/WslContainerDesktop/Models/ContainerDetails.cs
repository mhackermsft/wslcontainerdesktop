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
    public string Command { get; init; } = "-";
    public string IpAddress { get; init; } = "-";
    public string StartedAt { get; init; } = "-";
    public string WorkingDir { get; init; } = "-";
    public string NetworkMode { get; init; } = "-";
    public IReadOnlyList<string> Environment { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Mounts { get; init; } = Array.Empty<string>();

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
            if (el.TryGetProperty("NetworkSettings", out var ns) &&
                ns.TryGetProperty("Networks", out var nets) &&
                nets.ValueKind == JsonValueKind.Object)
            {
                foreach (var net in nets.EnumerateObject())
                {
                    networkMode = net.Name;
                    if (net.Value.TryGetProperty("IPAddress", out var ipEl) &&
                        ipEl.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(ipEl.GetString()))
                    {
                        ip = ipEl.GetString()!;
                    }

                    break;
                }
            }

            if (networkMode == "-" &&
                el.TryGetProperty("HostConfig", out var hc) &&
                hc.TryGetProperty("NetworkMode", out var nm) &&
                nm.ValueKind == JsonValueKind.String)
            {
                networkMode = nm.GetString() ?? "-";
            }

            var mounts = new List<string>();
            if (el.TryGetProperty("Mounts", out var mountsEl) && mountsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mountsEl.EnumerateArray())
                {
                    var src = m.TryGetProperty("Source", out var s) ? s.GetString() : null;
                    var dst = m.TryGetProperty("Destination", out var d) ? d.GetString() : null;
                    if (!string.IsNullOrEmpty(dst))
                    {
                        mounts.Add(string.IsNullOrEmpty(src) ? dst! : $"{src} -> {dst}");
                    }
                }
            }

            return new ContainerDetails
            {
                Command = command,
                WorkingDir = workingDir,
                Environment = env,
                StartedAt = startedAt,
                IpAddress = ip,
                NetworkMode = networkMode,
                Mounts = mounts,
            };
        }
        catch
        {
            return new ContainerDetails();
        }
    }
}
