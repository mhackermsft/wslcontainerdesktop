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


using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed class ResolvedDevContainerFeature
{
    public string Id { get; init; } = string.Empty;
    public string DirectoryPath { get; init; } = string.Empty;
    public Dictionary<string, string> ContainerEnv { get; init; } = new(StringComparer.Ordinal);
    public string? RemoteUser { get; set; }
    public List<string> InstallsAfter { get; init; } = new();
}

public sealed class DevContainerFeatureResolution
{
    public IReadOnlyList<ResolvedDevContainerFeature> Features { get; init; } = Array.Empty<ResolvedDevContainerFeature>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class DevContainerDerivedImage
{
    public string ContextPath { get; init; } = string.Empty;
    public string DockerfilePath { get; init; } = string.Empty;
    public string ImageTag { get; init; } = string.Empty;
    public Dictionary<string, string> ContainerEnv { get; init; } = new(StringComparer.Ordinal);
    public string? RemoteUser { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public interface IDevContainerFeatureResolver
{
    Task<DevContainerDerivedImage?> PrepareDerivedImageAsync(
        DevContainerConfig config,
        string baseImage,
        CancellationToken ct = default);
}
