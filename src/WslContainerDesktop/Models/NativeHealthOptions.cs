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

namespace WslContainerDesktop.Models;

/// <summary>Desired creation-time health configuration; null means inherit the image check.</summary>
public sealed class NativeHealthOptions
{
    /// <summary>Docker test form: CMD with argv, CMD-SHELL with one script, or NONE.</summary>
    public List<string> Test { get; set; } = new();
    public bool Disabled { get; set; }
    public string? Interval { get; set; }
    public string? Timeout { get; set; }
    public string? StartPeriod { get; set; }
    public string? StartInterval { get; set; }
    public int? Retries { get; set; }

    public bool IsDisabled => Disabled || Test.FirstOrDefault() == "NONE";
    public bool HasCommand => Test.Count > 1 && Test[0] is "CMD" or "CMD-SHELL";

    public NativeHealthOptions Clone() => new()
    {
        Test = new(Test), Disabled = Disabled, Interval = Interval, Timeout = Timeout,
        StartPeriod = StartPeriod, StartInterval = StartInterval, Retries = Retries,
    };
}
