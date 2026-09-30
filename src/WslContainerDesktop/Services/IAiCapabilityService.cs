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

/// <summary>
/// Caches and refreshes AI provider capability evidence for Settings and assistant chat. It keeps
/// metadata reads and optional harmless probes separate so the UI can distinguish unknown support
/// from observed support.
/// </summary>
public interface IAiCapabilityService
{
    /// <summary>Returns the current cached observation for <paramref name="configuration"/> without probing.</summary>
    AiCapabilitySnapshot GetCached(AiChatConfiguration configuration);
    /// <summary>Refreshes metadata and optionally runs harmless probes for <paramref name="configuration"/>.</summary>
    Task<AiCapabilitySnapshot> GetAsync(AiChatConfiguration configuration,
        bool probe = false, CancellationToken ct = default);
    /// <summary>Clears cached observations after settings or credentials change.</summary>
    void Invalidate();

    /// <summary>
    /// Raised when the cached observation changes, so status shown in the UI reflects what the app
    /// has actually observed. Without it a turn could observe tool support and act on it while the
    /// assistant badge still showed the stale caution state that preceded it.
    /// </summary>
    event EventHandler? Changed;
}

/// <summary>
/// Shared seam for HTTP, Copilot and later runtime adapters. Metadata must not generate, load or
/// download a model. Probes may generate bounded synthetic content, but never invoke app tools.
/// Caller cancellation must propagate. Return only typed observations, never raw server evidence.
/// </summary>
public interface IAiCapabilityObserver
{
    /// <summary>Provider kind this observer knows how to inspect.</summary>
    AiProviderKind Kind { get; }
    /// <summary>Reads passive metadata without causing model generation, load, or download.</summary>
    Task<AiCapabilitySnapshot> ReadMetadataAsync(AiChatConfiguration configuration, CancellationToken ct);
    /// <summary>Runs bounded harmless generation checks starting from previously read metadata.</summary>
    Task<AiCapabilitySnapshot> ProbeAsync(AiCapabilitySnapshot metadata, CancellationToken ct);
}
