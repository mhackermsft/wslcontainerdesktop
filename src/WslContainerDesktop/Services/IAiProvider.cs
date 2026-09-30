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

/// <summary>Adapter contract for an assistant provider that can answer diagnostics and may support chat turns.</summary>
public interface IAiProvider
{
    /// <summary>Provider kind selected in settings and used to choose this adapter.</summary>
    AiProviderKind Kind { get; }

    /// <summary>Friendly provider name shown in UI messages.</summary>
    string DisplayName { get; }

    /// <summary>Sends one sanitized diagnostic prompt and returns the parsed diagnosis.</summary>
    Task<AiDiagnosis> CompleteAsync(AiPromptRequest request, CancellationToken ct);

    /// <summary>Performs a lightweight provider-specific health check and returns a status message.</summary>
    Task<string> TestAsync(CancellationToken ct);
}

/// <summary>Service used by the UI to preview, send, and test AI-powered container diagnostics.</summary>
public interface IAiDiagnosticsService
{
    /// <summary>Collects sanitized evidence for the selected container without sending it.</summary>
    Task<AiDiagnosticPreview> BuildPreviewAsync(ContainerInfo container, CancellationToken ct = default);

    /// <summary>Sends a previously previewed prompt to the currently selected provider.</summary>
    Task<AiDiagnosis> DiagnoseAsync(AiPromptRequest request, CancellationToken ct = default);

    /// <summary>Tests the configured provider and returns a human-readable status.</summary>
    Task<string> TestProviderAsync(CancellationToken ct = default);
}
