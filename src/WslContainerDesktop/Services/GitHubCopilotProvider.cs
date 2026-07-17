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
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Windows.Storage;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed class GitHubCopilotProvider(
    ISettingsService settings,
    IAiCredentialStore credentials,
    ILogger<GitHubCopilotProvider> logger) : IAiProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);
    private const string DefaultModel = "auto";

    public AiProviderKind Kind => AiProviderKind.GitHubCopilot;

    public string DisplayName => "GitHub Copilot";

    public async Task<AiDiagnosis> CompleteAsync(AiPromptRequest request, CancellationToken ct)
    {
        var result = await RunCopilotAsync(request, ct).ConfigureAwait(false);
        return AiProviderJson.ParseDiagnosis(result.Content);
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        var result = await RunCopilotAsync(new AiPromptRequest(
            "Return JSON only.",
            "Return {\"summary\":\"ok\",\"likelyCause\":\"configured\",\"evidenceCited\":[],\"suggestedFix\":{\"description\":\"none\",\"commands\":[],\"fileEdits\":[]},\"confidence\":1}"), ct).ConfigureAwait(false);
        return $"GitHub Copilot responded using model '{result.Model}'.";
    }

    private async Task<CopilotRunResult> RunCopilotAsync(AiPromptRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            await using var client = CreateClient();
            await client.StartAsync(timeout.Token).ConfigureAwait(false);
            var model = await ResolveModelAsync(client, timeout.Token).ConfigureAwait(false);

            await using var session = await client.CreateSessionAsync(new SessionConfig
            {
                Model = model,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Replace,
                    Content = request.SystemPrompt,
                },
                AvailableTools = new List<string>(),
                InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
                EnableSkills = false,
                EnableConfigDiscovery = false,
                SkipCustomInstructions = true,
                EnableHostGitOperations = false,
                EnableSessionStore = false,
                WorkingDirectory = SafeWorkingDirectory(),
            }, timeout.Token).ConfigureAwait(false);

            var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var content = new StringBuilder();
            using var registration = timeout.Token.Register(() => done.TrySetCanceled(timeout.Token));
            using var subscription = session.On<SessionEvent>(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent message:
                        content.Append(message.Data.Content);
                        break;
                    case SessionErrorEvent error:
                        done.TrySetException(new InvalidOperationException(error.Data.Message));
                        break;
                    case SessionIdleEvent:
                        done.TrySetResult(null);
                        break;
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = request.UserPrompt }, timeout.Token).ConfigureAwait(false);
            await done.Task.ConfigureAwait(false);
            return new CopilotRunResult(content.ToString(), model);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("GitHub Copilot did not finish before the diagnostics timeout.");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GitHub Copilot diagnostics failed.");
            throw new InvalidOperationException(
                "GitHub Copilot is not ready. Sign in to the Copilot CLI (or save a GitHub token in Settings), " +
                "verify Copilot entitlement, and ensure the installed Copilot CLI is available. Details: " + ex.Message,
                ex);
        }
    }

    private CopilotClient CreateClient()
    {
        var hasToken = credentials.TryReadSecret(AiProviderKind.GitHubCopilot, out var token)
            && !string.IsNullOrWhiteSpace(token);
        return new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForStdio(FindCopilotCliPath()),
            GitHubToken = hasToken ? token : null,
            UseLoggedInUser = hasToken ? false : true,
            BaseDirectory = SafeBaseDirectory(),
            WorkingDirectory = SafeWorkingDirectory(),
            Logger = logger,
        });
    }

    private async Task<string> ResolveModelAsync(CopilotClient client, CancellationToken ct)
    {
        var requested = string.IsNullOrWhiteSpace(settings.AiGitHubCopilotModel)
            ? DefaultModel
            : settings.AiGitHubCopilotModel.Trim();

        try
        {
            var models = await client.ListModelsAsync(ct).ConfigureAwait(false);
            if (models.Any(m => string.Equals(m.Id, requested, StringComparison.OrdinalIgnoreCase)))
            {
                return requested;
            }

            var fallback = models.FirstOrDefault(m => string.Equals(m.Id, DefaultModel, StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault();
            if (fallback is not null)
            {
                logger.LogDebug("GitHub Copilot model {RequestedModel} is unavailable; falling back to {FallbackModel}.", requested, fallback.Id);
                return fallback.Id;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to list GitHub Copilot models; using configured model {Model}.", requested);
        }

        return requested;
    }

    private static string? FindCopilotCliPath()
    {
        var candidates = new List<string?>();
        candidates.Add(Environment.GetEnvironmentVariable("COPILOT_CLI_PATH"));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "copilot.exe"));
        candidates.Add(Path.Combine(appData, "npm", "copilot.cmd"));

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(directory, "copilot.exe"));
            candidates.Add(Path.Combine(directory, "copilot.cmd"));
        }

        return candidates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .FirstOrDefault(File.Exists);
    }

    private static string SafeBaseDirectory() => Path.Combine(SafeLocalCache(), "copilot-sdk");

    private static string SafeWorkingDirectory() => SafeLocalCache();

    private static string SafeLocalCache()
    {
        try
        {
            return ApplicationData.Current.LocalCacheFolder.Path;
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslContainerDesktop");
        }
    }

    private sealed record CopilotRunResult(string Content, string Model);
}
