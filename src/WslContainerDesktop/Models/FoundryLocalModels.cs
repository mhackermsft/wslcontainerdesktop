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

/// <summary>Advertised data, not an artifact audit or a hardware compatibility guarantee.</summary>
public sealed record FoundryLocalModel(
    string Id, string Version, string Task, string ModelType, string DeviceType,
    string ExecutionProvider, double? FileSizeMb, string License, string LicenseDescription,
    bool? SupportsToolCalling);

public sealed record FoundryLocalInventory(
    AiChatConfiguration Configuration, IReadOnlyList<FoundryLocalModel> Catalog,
    IReadOnlyList<string> Cached, IReadOnlyList<string> Loaded, string RuntimeIdentity)
{
    public FoundryLocalModel? Selected => Catalog.SingleOrDefault(m => m.Id == Configuration.Model);
    public bool IsCached => Cached.Contains(Configuration.Model, StringComparer.Ordinal);
    public bool IsLoaded => Loaded.Contains(Configuration.Model, StringComparer.Ordinal);
}

public sealed record FoundryLocalMutationResult(bool IsConfirmed, LocalRuntimeResourceState Memory,
    LocalRuntimeResourceState ModelData, string Guidance);
