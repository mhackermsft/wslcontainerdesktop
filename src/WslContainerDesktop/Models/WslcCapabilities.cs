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

using System.Collections.Frozen;

namespace WslContainerDesktop.Models;

/// <summary>
/// Immutable availability evidence, not an operation result. Choose a backend before mutating;
/// never retry a failed native operation through a legacy backend.
/// </summary>
public sealed class WslcCapabilities
{
    private readonly FrozenDictionary<WslcFeature, WslcCapability> _features;

    public WslcCapabilities(
        string executablePath,
        string? version,
        IReadOnlyDictionary<WslcFeature, WslcCapability> features,
        string? versionDiagnostic = null)
    {
        ExecutablePath = executablePath;
        Version = version;
        VersionDiagnostic = versionDiagnostic;
        _features = features.ToFrozenDictionary();
    }

    public string ExecutablePath { get; }
    public string? Version { get; }
    public string? VersionDiagnostic { get; }

    public WslcCapability this[WslcFeature feature] => _features.TryGetValue(feature, out var capability)
        ? capability
        : new(WslcCapabilitySupport.Unknown, $"No capability evidence for {feature}.");

    /// <summary>False includes Unknown; inspect Support/Diagnostic before selecting a fallback.</summary>
    public bool IsSupported(WslcFeature feature) => this[feature].Support == WslcCapabilitySupport.Supported;

    public bool HasProbeFailures => VersionDiagnostic is not null ||
        Enum.GetValues<WslcFeature>().Any(feature => this[feature].Support == WslcCapabilitySupport.Unknown);
}
