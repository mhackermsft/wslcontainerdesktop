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

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed partial class AiDiagnosticsService(
    IWslcService wslc,
    IActivityLog activity,
    ISettingsService settings,
    IEnumerable<IAiProvider> providers,
    ILogger<AiDiagnosticsService> logger) : IAiDiagnosticsService
{
    private const int LogTail = 300;
    private const int MaxSectionChars = 16_000;
    private const int MaxDiffEntries = 120;

    public async Task<AiDiagnosticPreview> BuildPreviewAsync(ContainerInfo container, CancellationToken ct = default)
    {
        var evidence = new StringBuilder();
        Append(evidence, "Question", container.State is ContainerState.Stopped or ContainerState.Created
            ? "Why did this container exit? Diagnose the most likely cause and propose review-only fixes."
            : "Analyze recent logs and container metadata for likely problems and propose review-only fixes.");

        Append(evidence, "Container", $"""
            Name: {container.Name}
            Id: {container.Id}
            Image: {container.Image}
            State: {container.State}
            CreatedUtc: {container.CreatedUtc:u}
            StateChangedUtc: {container.StateChangedUtc:u}
            Ports: {string.Join(", ", container.Ports.Select(p => p.Display))}
            """);

        await AddCommandSectionAsync(evidence, "Recent logs", () => wslc.GetLogsAsync(container.Id, LogTail, ct)).ConfigureAwait(false);
        await AddCommandSectionAsync(evidence, "Inspect JSON", () => wslc.InspectContainerAsync(container.Id, ct)).ConfigureAwait(false);

        if (container.State == ContainerState.Running)
        {
            try
            {
                var changes = await wslc.DiffContainerAsync(container.Id, container.Image, ct).ConfigureAwait(false);
                Append(evidence, "Filesystem changes", string.Join('\n', changes.Take(MaxDiffEntries).Select(c => $"{c.KindGlyph} {c.Path}")));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "AI diagnostics filesystem diff failed.");
                Append(evidence, "Filesystem changes", "Unavailable.");
            }
        }

        var recent = activity.Events
            .Where(e => e.Category == ActivityCategory.Container
                && (e.Title.Contains(container.Name, StringComparison.OrdinalIgnoreCase)
                    || (e.Detail?.Contains(container.ShortId, StringComparison.OrdinalIgnoreCase) ?? false)))
            .Take(20)
            .Select(e => $"{e.Timestamp:u} {e.Title} {e.Detail}".Trim());
        Append(evidence, "Recent activity", string.Join('\n', recent));

        var payload = Redact(Truncate(evidence.ToString(), 48_000));
        return new AiDiagnosticPreview(new AiPromptRequest(SystemPrompt, payload), payload);
    }

    public async Task<AiDiagnosis> DiagnoseAsync(AiPromptRequest request, CancellationToken ct = default)
    {
        if (!settings.AiFeaturesEnabled)
        {
            throw new InvalidOperationException("AI features are off. Enable them in Settings before sending diagnostics.");
        }

        if (settings.AiProvider == AiProviderKind.None)
        {
            throw new InvalidOperationException("Choose an AI provider in Settings before sending diagnostics.");
        }

        var provider = providers.FirstOrDefault(p => p.Kind == settings.AiProvider)
            ?? throw new InvalidOperationException($"AI provider '{settings.AiProvider}' is not registered.");
        return await provider.CompleteAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<string> TestProviderAsync(CancellationToken ct = default)
    {
        if (!settings.AiFeaturesEnabled)
        {
            throw new InvalidOperationException("AI features are off.");
        }

        if (settings.AiProvider == AiProviderKind.None)
        {
            throw new InvalidOperationException("Choose an AI provider first.");
        }

        var provider = providers.FirstOrDefault(p => p.Kind == settings.AiProvider)
            ?? throw new InvalidOperationException($"AI provider '{settings.AiProvider}' is not registered.");
        return await provider.TestAsync(ct).ConfigureAwait(false);
    }

    private async Task AddCommandSectionAsync(StringBuilder builder, string title, Func<Task<CommandResult>> command)
    {
        try
        {
            var result = await command().ConfigureAwait(false);
            var text = result.Success ? result.StandardOutput : result.ErrorText;
            Append(builder, title, text);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AI diagnostics evidence collection failed for {Section}.", title);
            Append(builder, title, "Unavailable.");
        }
    }

    private static void Append(StringBuilder builder, string title, string? content)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine(Truncate(string.IsNullOrWhiteSpace(content) ? "(none)" : content.Trim(), MaxSectionChars));
        builder.AppendLine();
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var head = maxChars / 2;
        var tail = maxChars - head;
        return text[..head] + "\n...[truncated]...\n" + text[^tail..];
    }

    private static string Redact(string text)
    {
        var redacted = SecretAssignmentRegex().Replace(text, "$1=<redacted>");
        redacted = ConnectionStringRegex().Replace(redacted, "$1=<redacted>");
        return BearerRegex().Replace(redacted, "$1 <redacted>");
    }

    private const string SystemPrompt = """
        You are a container-debugging assistant inside WSL Container Desktop.
        Use only the provided evidence. Cite concrete log lines, inspect fields, state, events, or diff entries.
        If evidence is insufficient, say exactly what is missing.
        Suggested commands and file edits are review-only; never imply they were executed.
        Return one JSON object with this schema and no extra prose:
        {
          "summary": "plain-language diagnosis",
          "likelyCause": "most likely cause or insufficient evidence",
          "evidenceCited": ["evidence item"],
          "suggestedFix": {
            "description": "what the user should review",
            "commands": ["optional commands to copy, never executed"],
            "fileEdits": ["optional file edits to review"]
          },
          "confidence": 0.0
        }
        """;

    [GeneratedRegex(@"(?im)\b([A-Z0-9_]*(?:TOKEN|KEY|SECRET|PASSWORD|PASSWD|PWD)[A-Z0-9_]*\s*[:=]\s*)([^\s,;""']+|""[^""]*""|'[^']*')")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(?im)\b((?:DefaultEndpointsProtocol|AccountKey|SharedAccessKey|Password|User ID|Uid|Pwd)\s*=\s*)([^;,\s]+)")]
    private static partial Regex ConnectionStringRegex();

    [GeneratedRegex(@"(?im)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerRegex();
}
