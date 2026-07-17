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

public sealed class GitHubCopilotProvider : IAiProvider
{
    public AiProviderKind Kind => AiProviderKind.GitHubCopilot;

    public string DisplayName => "GitHub Copilot";

    public Task<AiDiagnosis> CompleteAsync(AiPromptRequest request, CancellationToken ct) =>
        throw NotSupported();

    public Task<string> TestAsync(CancellationToken ct) =>
        throw NotSupported();

    public Task<string> SignInAsync(CancellationToken ct) =>
        throw NotSupported();

    private static NotSupportedException NotSupported() => new(
        "GitHub Copilot programmatic chat is isolated behind this provider seam, but the app has " +
        "not yet wired the GitHub.Copilot.SDK client/auth flow. Use Ollama, Azure OpenAI, or OpenAI " +
        "for now; the SDK package/endpoint can be connected here without changing the diagnostics UI.");
}
