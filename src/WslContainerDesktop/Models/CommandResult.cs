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

namespace WslContainerDesktop.Models;

/// <summary>Result of running a wslc process: exit code and captured streams.</summary>
public sealed class CommandResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool Success => ExitCode == 0;

    /// <summary>Best-effort human readable error text.</summary>
    public string ErrorText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(StandardError))
            {
                return StandardError.Trim();
            }

            return string.IsNullOrWhiteSpace(StandardOutput)
                ? $"Command failed with exit code {ExitCode}."
                : StandardOutput.Trim();
        }
    }
}
