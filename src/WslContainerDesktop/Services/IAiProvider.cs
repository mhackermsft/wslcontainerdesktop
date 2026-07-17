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

public interface IAiProvider
{
    AiProviderKind Kind { get; }

    string DisplayName { get; }

    Task<AiDiagnosis> CompleteAsync(AiPromptRequest request, CancellationToken ct);

    Task<string> TestAsync(CancellationToken ct);
}

public interface IAiDiagnosticsService
{
    Task<AiDiagnosticPreview> BuildPreviewAsync(ContainerInfo container, CancellationToken ct = default);

    Task<AiDiagnosis> DiagnoseAsync(AiPromptRequest request, CancellationToken ct = default);

    Task<string> TestProviderAsync(CancellationToken ct = default);
}
