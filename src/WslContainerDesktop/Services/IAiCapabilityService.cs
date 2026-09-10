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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public interface IAiCapabilityService
{
    AiCapabilitySnapshot GetCached(AiChatConfiguration configuration);
    Task<AiCapabilitySnapshot> GetAsync(AiChatConfiguration configuration,
        bool probe = false, CancellationToken ct = default);
    void Invalidate();
}

/// <summary>
/// Shared seam for HTTP, Copilot and later runtime adapters. Metadata must not generate, load or
/// download a model. Probes may generate bounded synthetic content, but never invoke app tools.
/// Caller cancellation must propagate. Return only typed observations, never raw server evidence.
/// </summary>
public interface IAiCapabilityObserver
{
    AiProviderKind Kind { get; }
    Task<AiCapabilitySnapshot> ReadMetadataAsync(AiChatConfiguration configuration, CancellationToken ct);
    Task<AiCapabilitySnapshot> ProbeAsync(AiCapabilitySnapshot metadata, CancellationToken ct);
}
