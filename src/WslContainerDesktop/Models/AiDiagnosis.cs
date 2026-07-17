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

public sealed class AiDiagnosis
{
    public string Summary { get; set; } = string.Empty;

    public string LikelyCause { get; set; } = string.Empty;

    public List<string> EvidenceCited { get; set; } = new();

    public AiSuggestedFix SuggestedFix { get; set; } = new();

    public double Confidence { get; set; }
}

public sealed class AiSuggestedFix
{
    public string Description { get; set; } = string.Empty;

    public List<string> Commands { get; set; } = new();

    public List<string> FileEdits { get; set; } = new();
}

public enum AiProviderKind
{
    None,
    GitHubCopilot,
    Ollama,
    AzureOpenAi,
    OpenAi,
}

public sealed record AiPromptRequest(string SystemPrompt, string UserPrompt);

public sealed record AiDiagnosticPreview(AiPromptRequest Request, string Payload);
