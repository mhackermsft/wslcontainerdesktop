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

/// <summary>An Azure subscription returned by `az account list`.</summary>
public sealed class AzureSubscription
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }

    public override string ToString() => Name;
}

/// <summary>An Azure Container Registry returned by `az acr list`.</summary>
public sealed class AzureRegistry
{
    public string Name { get; init; } = string.Empty;
    public string LoginServer { get; init; } = string.Empty;
    public string ResourceGroup { get; init; } = string.Empty;
    public bool AdminEnabled { get; init; }

    public override string ToString() => $"{Name} ({LoginServer})";
}
