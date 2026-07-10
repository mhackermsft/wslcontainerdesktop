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

using System.Text.Json.Serialization;

namespace WslContainerDesktop.Models;

/// <summary>
/// A live resource-usage row from `wslc stats --format json`. All numeric fields are
/// returned by the CLI as pre-formatted strings (e.g. "100.07%", "1.51 MiB / 31.16 GiB").
/// </summary>
public sealed class ContainerStats
{
    [JsonPropertyName("ID")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("CPUPerc")]
    public string CpuPercent { get; set; } = "0%";

    [JsonPropertyName("MemPerc")]
    public string MemPercent { get; set; } = "0%";

    [JsonPropertyName("MemUsage")]
    public string MemUsage { get; set; } = "-";

    [JsonPropertyName("NetIO")]
    public string NetIO { get; set; } = "-";

    [JsonPropertyName("BlockIO")]
    public string BlockIO { get; set; } = "-";

    [JsonPropertyName("PIDs")]
    public int Pids { get; set; }

    [JsonIgnore]
    public double CpuValue => ParsePercent(CpuPercent);

    [JsonIgnore]
    public double MemValue => ParsePercent(MemPercent);

    private static double ParsePercent(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return 0;
        }

        var trimmed = s.Replace("%", string.Empty).Trim();
        return double.TryParse(trimmed, out var v) ? v : 0;
    }
}
