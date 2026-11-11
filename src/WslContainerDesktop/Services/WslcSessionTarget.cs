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

namespace WslContainerDesktop.Services;

/// <summary>An immutable command target, independent of the current UI session selection.</summary>
public sealed record WslcSessionTarget
{
    /// <summary>The default CLI session, selected by omitting the session option.</summary>
    public static WslcSessionTarget Default { get; } = new((string?)null);

    /// <summary>Creates a target for a named session, or for the default CLI session when null.</summary>
    public WslcSessionTarget(string? name)
    {
        if (name is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
    }

    /// <summary>The exact engine session name, or null for the default CLI session.</summary>
    public string? Name { get; }

    /// <summary>Adds the session option without modifying the original argument sequence.</summary>
    public IEnumerable<string> AddSessionSelector(IEnumerable<string> arguments) =>
        Name is null ? arguments : ["--session", Name, .. arguments];
}
