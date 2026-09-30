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

/// <summary>Model object that stores ai diagnosis information used by services, view models, or dialogs.</summary>
public sealed class AiDiagnosis
{
    /// <summary>Gets or sets the summary.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets the likely cause.</summary>
    public string LikelyCause { get; set; } = string.Empty;

    /// <summary>Gets or sets the evidence cited.</summary>
    public List<string> EvidenceCited { get; set; } = new();

    /// <summary>Gets or sets the suggested fix.</summary>
    public AiSuggestedFix SuggestedFix { get; set; } = new();

    /// <summary>Gets or sets the confidence.</summary>
    public double Confidence { get; set; }
}

/// <summary>Model object that stores ai suggested fix information used by services, view models, or dialogs.</summary>
public sealed class AiSuggestedFix
{
    /// <summary>Gets or sets the description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the commands.</summary>
    public List<string> Commands { get; set; } = new();

    /// <summary>Gets or sets the file edits.</summary>
    public List<string> FileEdits { get; set; } = new();
}

/// <summary>Values that describe ai provider kind states or choices in WSL Container Desktop workflows.</summary>
public enum AiProviderKind
{
    /// <summary>Represents the none option.</summary>
    None,
    /// <summary>Represents the git hub copilot option.</summary>
    GitHubCopilot,
    /// <summary>Represents the ollama option.</summary>
    Ollama,
    /// <summary>Represents the azure open ai option.</summary>
    AzureOpenAi,
    /// <summary>Represents the open ai option.</summary>
    OpenAi,
    /// <summary>Represents the foundry local option.</summary>
    FoundryLocal,
}

/// <summary>Friendly display name for an <see cref="AiProviderKind"/>, shared by provider
/// implementations and AI feedback/error classification so the wording stays consistent.</summary>
public static class AiProviderKindExtensions
{
    /// <summary>Performs the display name helper used by this model or dialog.</summary>
    /// <param name="kind">The kind value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public static string DisplayName(this AiProviderKind kind) => kind switch
    {
        AiProviderKind.GitHubCopilot => "GitHub Copilot",
        AiProviderKind.Ollama => "Ollama",
        AiProviderKind.AzureOpenAi => "Azure OpenAI",
        AiProviderKind.OpenAi => "OpenAI",
        AiProviderKind.FoundryLocal => "Foundry Local",
        _ => "AI",
    };
}

/// <summary>Immutable or init-only data model that carries ai prompt request information between services and view models.</summary>
public sealed record AiPromptRequest(string SystemPrompt, string UserPrompt);

/// <summary>Immutable or init-only data model that carries ai diagnostic preview information between services and view models.</summary>
public sealed record AiDiagnosticPreview(AiPromptRequest Request, string Payload);
