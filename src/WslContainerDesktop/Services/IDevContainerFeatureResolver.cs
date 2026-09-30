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

/// <summary>
/// Metadata for one resolved Dev Container Feature staged into the temporary Docker build context.
/// </summary>
public sealed class ResolvedDevContainerFeature
{
    /// <summary>Feature identifier from the devcontainer configuration.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Folder containing the feature's install script and metadata.</summary>
    public string DirectoryPath { get; init; } = string.Empty;
    /// <summary>Environment variables contributed by this feature for the final container.</summary>
    public Dictionary<string, string> ContainerEnv { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Optional remote user requested by the feature metadata.</summary>
    public string? RemoteUser { get; set; }
    /// <summary>Feature ids that this feature prefers to install after.</summary>
    public List<string> InstallsAfter { get; init; } = new();
}

/// <summary>Resolved feature list plus non-fatal warnings from feature lookup.</summary>
public sealed class DevContainerFeatureResolution
{
    /// <summary>Features that were successfully staged.</summary>
    public IReadOnlyList<ResolvedDevContainerFeature> Features { get; init; } = Array.Empty<ResolvedDevContainerFeature>();
    /// <summary>Warnings for skipped or partially read features.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Build context information for the derived image that layers features on a base image.</summary>
public sealed class DevContainerDerivedImage
{
    /// <summary>Folder passed as the image build context.</summary>
    public string ContextPath { get; init; } = string.Empty;
    /// <summary>Generated Dockerfile path within the context.</summary>
    public string DockerfilePath { get; init; } = string.Empty;
    /// <summary>Local tag assigned to the derived image.</summary>
    public string ImageTag { get; init; } = string.Empty;
    /// <summary>Environment variables merged into the final dev container.</summary>
    public Dictionary<string, string> ContainerEnv { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Final remote user selected from feature metadata, if any.</summary>
    public string? RemoteUser { get; init; }
    /// <summary>Warnings that should be shown with the prepared image.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Builds a feature-layered image context for imported Dev Container definitions.</summary>
public interface IDevContainerFeatureResolver
{
    /// <summary>Stages requested features on top of <paramref name="baseImage"/> or returns null when no feature image is needed.</summary>
    Task<DevContainerDerivedImage?> PrepareDerivedImageAsync(
        DevContainerConfig config,
        string baseImage,
        CancellationToken ct = default);
}
