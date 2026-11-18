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
using System.Text.Json;
using Microsoft.Copilot;
using Microsoft.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

#pragma warning disable MCPSDK001 // Required SDK permission hook so Copilot surfaces our declared tool calls to the app gate.
/// <summary>
/// AI provider implementation that uses the installed GitHub Copilot CLI and Copilot Chat SDK to answer assistant turns.
/// </summary>
public sealed class GitHubCopilotProvider(
    ISettingsService settings,
    ILogger<GitHubCopilotProvider> logger) : IAiProvider, IAiChatProvider, IAiCapabilityObserver
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);
    private const string DefaultModel = "auto";

    /// <summary>Message shown when the selected Copilot CLI speaks a different SDK protocol version.</summary>
    internal const string ProtocolMismatchMessage =
        "The selected Copilot CLI is not compatible with this version of WSL Container Desktop. In Settings, choose a different Copilot CLI or install the compatible version.";

    /// <inheritdoc/>
    public AiProviderKind Kind => AiProviderKind.GitHubCopilot;

    /// <inheritdoc/>
    public string DisplayName => Kind.DisplayName();

    /// <inheritdoc/>
    public async Task<AiCapabilitySnapshot> ReadMetadataAsync(AiChatConfiguration configuration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var state = new AiCapabilitySnapshot(configuration);
        if (FindCopilotCliPath(settings.AiGitHubCopilotCliPath) is not { } path)
            return state with { Runtime = AiRuntimeState.Unavailable };
        try
        {
            await using var client = CreateClient(logger, path);
            await client.StartAsync(ct).ConfigureAwait(false);
            var models = await client.ListModelsAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            return state with
            {
                Endpoint = AiEndpointState.Reachable, Runtime = AiRuntimeState.Ready,
                Model = ClassifyModel(models.Select(m => m.Id), configuration.Model),
                RuntimeIdentity = AiCapabilityService.HashIdentity($"{file.Length}:{file.LastWriteTimeUtc.Ticks}"),
                // SDK inventory does not establish tool/JSON support for a selected model.
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // CLI/entitlement/auth failures are not all authentication failures; remain explicit unknown.
            return state with { Runtime = AiRuntimeState.Unavailable };
        }
    }

    /// <inheritdoc/>
    public async Task<AiCapabilitySnapshot> ProbeAsync(AiCapabilitySnapshot metadata, CancellationToken ct)
    {
        try
        {
            return await CopilotCapabilityProbe.RunAsync(metadata, RunTurnAsync, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Do not retain SDK errors, prompts, callback evidence or exceptions in observations.
            return metadata;
        }
    }

    /// <inheritdoc/>
    public async Task<AiDiagnosis> CompleteAsync(AiPromptRequest request, CancellationToken ct)
    {
        var result = await RunCopilotAsync(request, "Diagnosis", ct).ConfigureAwait(false);
        return AiProviderJson.ParseDiagnosis(result.Content);
    }

    /// <inheritdoc/>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        var result = await RunCopilotAsync(new AiPromptRequest(
            "Return JSON only.",
            "Return {\"summary\":\"ok\",\"likelyCause\":\"configured\",\"evidenceCited\":[],\"suggestedFix\":{\"description\":\"none\",\"commands\":[],\"fileEdits\":[]},\"confidence\":1}"), "Provider test", ct).ConfigureAwait(false);
        return string.Equals(result.RequestedModel, result.ActualModel, StringComparison.OrdinalIgnoreCase)
            ? $"GitHub Copilot responded using model '{result.ActualModel}'."
            : $"GitHub Copilot responded using configured model '{result.RequestedModel}' (runtime reported '{result.ActualModel}').";
    }

    private async Task<CopilotRunResult> RunCopilotAsync(AiPromptRequest request, string operation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            await using var client = CreateClient();
            await client.StartAsync(timeout.Token).ConfigureAwait(false);
            var model = await ResolveModelAsync(client, timeout.Token).ConfigureAwait(false);

            await using var session = await client.CreateSessionAsync(new CreateSessionOptions
            {
                Model = model,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Replace,
                    Content = request.SystemPrompt,
                },
                Tools = new SessionToolOptions { AvailableTools = new List<string>() },
                State = new SessionStateOptions
                {
                    InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
                    EnableCrossSessionStore = false,
                },
                Skills = new SessionSkillOptions { Enabled = false },
                EnableConfigDiscovery = false,
                Instructions = new CreateSessionInstructionOptions { SkipCustomInstructions = true },
                Git = new SessionGitOptions { EnableHostGitOperations = false },
                WorkingDirectory = SafeWorkingDirectory(),
            }, timeout.Token).ConfigureAwait(false);

            var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var content = new StringBuilder();
            var actualModel = model;
            using var registration = timeout.Token.Register(() => done.TrySetCanceled(timeout.Token));
            using var subscription = session.On<SessionEvent>(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent message:
                        content.Append(message.Data.Content);
                        if (!string.IsNullOrWhiteSpace(message.Data.Model))
                        {
                            actualModel = message.Data.Model;
                        }
                        break;
                    case SessionErrorEvent error:
                        done.TrySetException(new InvalidOperationException(error.Data.Message));
                        break;
                    case SessionIdleEvent:
                        done.TrySetResult(null);
                        break;
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = AiTextSanitizer.Sanitize(request.UserPrompt, AiTextSanitizer.DiagnosticLimit) }, timeout.Token).ConfigureAwait(false);
            await done.Task.ConfigureAwait(false);
            return new CopilotRunResult(content.ToString(), model, actualModel);
        }
        catch (OperationCanceledException)
        {
            // Propagate as-is: AiErrorClassifier compares against the caller's own `ct` to tell a
            // user cancellation apart from this method's internal RequestTimeout expiring.
            throw;
        }
        catch (AiProviderException)
        {
            // Already classified (e.g. ResolveModelAsync's "model not available") — don't flatten
            // it into the generic "not ready" fallback below.
            throw;
        }
        catch (Exception ex) when (IsProtocolMismatch(ex))
        {
            throw ProtocolMismatch(operation, ex);
        }
        catch (Exception ex)
        {
            logger.LogDebug("GitHub Copilot diagnostics failed: {Detail}", AiTextSanitizer.Sanitize(ex.Message));
            throw new AiProviderException(
                Kind,
                operation,
                "GitHub Copilot is not ready. Sign in to the Copilot CLI, verify Copilot entitlement, and ensure the installed Copilot CLI is available.",
                AiFailureKind.Unexpected,
                responseDetail: ex.Message,
                inner: ex);
        }
    }

    /// <inheritdoc/>
    public async Task<string> RunTurnAsync(
        IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
        CancellationToken ct)
        => (await RunTurnAsync(new AiChatRequest(AiConversationContext.Capture(settings, Kind), history),
            tools, invokeToolAsync, ct).ConfigureAwait(false)).FinalText;

    /// <inheritdoc/>
    public Task<AiChatTurnResult> RunTurnAsync(
        AiChatRequest request,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
        CancellationToken ct)
        => new CopilotChatTurnRunner(RunChatSessionAsync).RunTurnAsync(request, tools, invokeToolAsync, ct);

    private async Task<string> RunChatSessionAsync(
        AiChatConfiguration configuration,
        IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
        Action<AiChatProgress> progress,
        Action<string> recordMessage,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? bridgeFailure = null;

        void FailBridge(Exception error)
        {
            Interlocked.CompareExchange(ref bridgeFailure, error, null);
            if (done.TrySetException(error))
            {
                try { timeout.Cancel(); }
                catch (ObjectDisposedException)
                {
                    // A late SDK callback can race session disposal; the failed completion is already latched.
                }
            }
        }

        try
        {
            await using var client = CreateClient();
            await client.StartAsync(timeout.Token).ConfigureAwait(false);
            var model = await ResolveModelAsync(client, timeout.Token, configuration.Model).ConfigureAwait(false);
            await using var session = await client.CreateSessionAsync(BuildChatSessionConfig(
                model, history, tools, async (call, callbackToken) =>
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref bridgeFailure) is not null)
                        throw new InvalidOperationException("The Copilot tool bridge has already failed.");
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, callbackToken);
                    return await invokeToolAsync(call, linked.Token).ConfigureAwait(false);
                }, FailBridge, SafeWorkingDirectory()), timeout.Token).ConfigureAwait(false);

            var prompt = BuildCopilotChatPrompt(history.Where(m => m.Role != "system"));
            string? finalText = null;
            var messages = new CopilotChatTurnRunner.MessageStream(progress, recordMessage);
            var eventGate = new object();
            using var registration = timeout.Token.Register(() => done.TrySetCanceled(timeout.Token));
            using var subscription = session.On<SessionEvent>(evt =>
            {
                lock (eventGate)
                {
                    if (done.Task.IsCompleted || timeout.IsCancellationRequested)
                        return;
                    try
                    {
                        switch (evt)
                        {
                            case AssistantMessageDeltaEvent delta:
                                progress(new(AiChatProgressKind.Generating, "Generating response…"));
                                messages.Append(delta.Data.MessageId ?? "", delta.Data.DeltaContent ?? "");
                                break;
                            case AssistantMessageEvent message:
                                messages.Complete(message.Data.MessageId ?? "", message.Data.Content ?? "");
                                finalText = AiTextSanitizer.Sanitize(message.Data.Content ?? "");
                                break;
                            case SessionErrorEvent:
                                FailBridge(new InvalidOperationException("GitHub Copilot reported a session error."));
                                break;
                            case SessionIdleEvent:
                                if (finalText is null || messages.HasPendingMessage)
                                    FailBridge(new InvalidOperationException("GitHub Copilot returned no assistant message."));
                                else
                                    done.TrySetResult(finalText);
                                break;
                        }
                    }
                    catch (Exception error)
                    {
                        FailBridge(error);
                    }
                }
            });
            await session.SendAsync(new MessageOptions { Prompt = prompt }, timeout.Token).ConfigureAwait(false);
            var result = await done.Task.ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return string.IsNullOrWhiteSpace(result) ? "Done." : result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested
            && Volatile.Read(ref bridgeFailure) is { } failure && failure is not OperationCanceledException)
        {
            throw new AiProviderException(
                Kind, "Assistant chat",
                "GitHub Copilot assistant chat failed before it could safely complete.",
                AiFailureKind.Configuration,
                responseDetail: failure.Message,
                inner: failure);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AiProviderException)
        {
            throw;
        }
        catch (Exception ex) when (IsProtocolMismatch(ex))
        {
            throw ProtocolMismatch("Assistant chat", ex);
        }
        catch (Exception ex)
        {
            logger.LogDebug("GitHub Copilot assistant chat failed: {Detail}", AiTextSanitizer.Sanitize(ex.Message));
            throw new AiProviderException(
                Kind,
                "Assistant chat",
                "GitHub Copilot assistant chat failed. Verify Copilot CLI sign-in, entitlement, and model/tool support.",
                AiFailureKind.Configuration,
                responseDetail: ex.Message,
                inner: ex);
        }
    }

    /// <summary>Builds the Copilot SDK session configuration from the app request and selected model.</summary>
    internal static CreateSessionOptions BuildChatSessionConfig(
        string model,
        IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
        Action<Exception> failBridge,
        string workingDirectory)
    {
        var allowlistedToolNames = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return new CreateSessionOptions
        {
            Model = model,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = string.Join("\n\n", history.Where(m => m.Role == "system").Select(m => m.Content)),
            },
            Events = new SessionEventOptions { Streaming = true },
            Tools = new SessionToolOptions
            {
                Definitions = BuildCopilotTools(tools, invokeToolAsync, failBridge),
                AvailableTools = tools.Select(t => t.Name).ToList(),
            },
            State = new SessionStateOptions
            {
                InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
                EnableCrossSessionStore = false,
            },
            Skills = new SessionSkillOptions { Enabled = false },
            EnableConfigDiscovery = false,
            Instructions = new CreateSessionInstructionOptions { SkipCustomInstructions = true },
            Git = new SessionGitOptions { EnableHostGitOperations = false },
            WorkingDirectory = workingDirectory,
            Permissions = new SessionPermissionOptions
            {
                OnRequest = (request, _) => Task.FromResult(HandleToolPermissionRequest(request, allowlistedToolNames)),
            },
        };
    }

    /// <summary>Converts provider-neutral tool definitions into Copilot SDK function declarations.</summary>
    internal static ICollection<AIFunctionDeclaration> BuildCopilotTools(
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
        Action<Exception> failBridge)
    {
        var declarations = new List<AIFunctionDeclaration>();
        foreach (var tool in tools)
        {
            declarations.Add(new DelegatingAssistantFunction(AiTextSanitizer.SanitizeDefinition(tool), invokeToolAsync, failBridge));
        }

        return declarations;
    }

    /// <summary>Maps Copilot SDK permission callbacks back to the app tool invocation policy.</summary>
    internal static PermissionDecision HandleToolPermissionRequest(
        PermissionRequest request,
        IReadOnlySet<string> allowlistedToolNames)
    {
        if (request is PermissionRequestCustomTool customTool
            && !string.IsNullOrWhiteSpace(customTool.ToolName)
            && allowlistedToolNames.Contains(customTool.ToolName))
        {
            return PermissionDecision.ApproveOnce();
        }

        return PermissionDecision.Reject("Only WSL Container Desktop's declared allowlisted assistant tools may run.");
    }

    /// <summary>Flattens the app chat history into the prompt format expected by the Copilot CLI path.</summary>
    internal static string BuildCopilotChatPrompt(IEnumerable<AiChatMessage> history)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Continue this tool-calling conversation. Use the declared tools when an action or live data is needed.");
        foreach (var message in history.Select(AiTextSanitizer.SanitizeMessage))
        {
            if (message.ToolCalls.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                    builder.AppendLine($"assistant: {message.Content}");
                builder.AppendLine($"assistant requested tools: {string.Join("; ", message.ToolCalls.Select(c => $"{c.Name}({c.ArgumentsJson}) call_id={c.Id}"))}");
                continue;
            }

            if (message.Role == "tool")
            {
                builder.AppendLine($"tool result for {message.ToolName} call_id={message.ToolCallId}: {message.Content}");
                continue;
            }

            builder.AppendLine($"{message.Role}: {message.Content}");
        }

        return builder.ToString();
    }

    /// <summary>Copilot SDK adapter that delegates a function call to the app tool callback.</summary>
    private sealed class DelegatingAssistantFunction(
        AiToolDefinition definition,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
        Action<Exception> failBridge) : AIFunction
    {
        private readonly JsonElement _schema = JsonDocument.Parse(definition.JsonSchemaParameters).RootElement.Clone();
        // Let the SDK bind its opaque context key. Missing SDK context must fail closed;
        // fabricated IDs or reconstructing JSON from model-facing arguments loses identity.
        private readonly AIFunction _binding = CopilotTool.DefineTool(
            async (ToolInvocation invocation, CancellationToken ct) =>
            {
                if (invocation.ToolName != definition.Name || invocation.Arguments is not { } arguments)
                    throw new InvalidOperationException("Copilot tool invocation context is missing or mismatched.");
                return await invokeToolAsync(new AiToolCall
                {
                    Id = invocation.ToolCallId,
                    Name = invocation.ToolName,
                    ArgumentsJson = arguments.GetRawText(),
                }, ct).ConfigureAwait(false);
            }, factoryOptions: new AIFunctionFactoryOptions { Name = definition.Name });

        /// <summary>Tool name advertised to Copilot.</summary>
        public override string Name => definition.Name;

        /// <summary>Tool description advertised to Copilot.</summary>
        public override string Description => definition.Description;

        /// <summary>JSON schema for the tool arguments advertised to Copilot.</summary>
        public override JsonElement JsonSchema => _schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await _binding.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failBridge(error);
                throw;
            }
        }
    }

    private CopilotClient CreateClient()
    {
        return CreateClient(logger, settings.AiGitHubCopilotCliPath);
    }

    /// <summary>
    /// Creates the Copilot SDK client for the Copilot CLI selected in settings (an empty selection means automatic
    /// discovery). The CLI runs as a child process over stdio, as it did before SDK v2 made in-process hosting the
    /// default, because the app does not ship the SDK's bundled runtime.
    /// </summary>
    internal static CopilotClient CreateClient(ILogger logger, string? configuredCliPath)
    {
        var cliPath = FindCopilotCliPath(configuredCliPath) ?? throw CliNotFound(configuredCliPath);
        var runtime = RuntimeConnection.ForSpawnedStdio(
            path: cliPath,
            workingDirectory: SafeWorkingDirectory(),
            baseDirectory: SafeBaseDirectory());
        return new CopilotClient(new CopilotClientOptions
        {
            // CodingAgent keeps the SDK v1 defaults; sessions still restrict tools to the app's allowlist.
            Mode = CopilotClientMode.CodingAgent,
            Runtime = runtime,
            GitHub = new ClientGitHubOptions { UseLoggedInUser = true },
            Observability = new ClientObservabilityOptions { Logger = AiTextSanitizer.WrapLogger(logger) },
        });
    }

    /// <summary>
    /// Finds the Copilot CLI to start: the configured path when one is set, otherwise the first automatic candidate
    /// (<c>COPILOT_CLI_PATH</c>, an app-installed CLI, WinGet, npm, then <c>PATH</c>).
    /// </summary>
    internal static string? FindCopilotCliPath(string? configuredCliPath = null) => CopilotCliLocator.Resolve(configuredCliPath);

    /// <summary>
    /// Classifies a configured model against the runtime's model list. <c>auto</c> lets Copilot choose and is never
    /// listed, so it is always available. Some runtimes return an empty list even when signed in and able to chat;
    /// an empty list cannot prove a model missing, so it is reported as unknown rather than rejected.
    /// </summary>
    internal static AiModelState ClassifyModel(IEnumerable<string> listedModelIds, string? configuredModel)
    {
        var requested = string.IsNullOrWhiteSpace(configuredModel) ? DefaultModel : configuredModel.Trim();
        if (string.Equals(requested, DefaultModel, StringComparison.OrdinalIgnoreCase))
            return AiModelState.Available;

        var listed = listedModelIds.ToList();
        if (listed.Count == 0)
            return AiModelState.Unknown;

        return listed.Any(id => string.Equals(id, requested, StringComparison.OrdinalIgnoreCase))
            ? AiModelState.Available
            : AiModelState.Missing;
    }

    /// <summary>Whether an exception (or one it wraps) is the SDK's protocol version negotiation failure.</summary>
    internal static bool IsProtocolMismatch(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("protocol version mismatch", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static AiProviderException ProtocolMismatch(string operation, Exception ex) => new(
        AiProviderKind.GitHubCopilot,
        operation,
        ProtocolMismatchMessage,
        AiFailureKind.Configuration,
        responseDetail: ex.Message,
        inner: ex);

    private static AiProviderException CliNotFound(string? configuredCliPath) => new(
        AiProviderKind.GitHubCopilot,
        "Start Copilot CLI",
        string.IsNullOrWhiteSpace(configuredCliPath)
            ? "No GitHub Copilot CLI was found. In Settings, install the compatible Copilot CLI or choose one."
            : $"The selected Copilot CLI no longer exists: {configuredCliPath}. In Settings, choose another Copilot CLI.",
        AiFailureKind.Configuration);

    private async Task<string> ResolveModelAsync(CopilotClient client, CancellationToken ct, string? configuredModel = null)
    {
        configuredModel ??= settings.AiGitHubCopilotModel;
        var requested = string.IsNullOrWhiteSpace(configuredModel)
            ? DefaultModel
            : configuredModel.Trim();

        try
        {
            var models = await client.ListModelsAsync(ct).ConfigureAwait(false);
            if (ClassifyModel(models.Select(m => m.Id), requested) != AiModelState.Missing)
            {
                return requested;
            }

            var available = string.Join(", ", models.Select(m => m.Id).Take(12));
            throw new AiProviderException(
                Kind,
                "Resolve model",
                $"GitHub Copilot model '{requested}' is not available. Choose one of the listed models in Settings. Available: {available}",
                AiFailureKind.Configuration);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Failed to validate GitHub Copilot model {Model}: {Detail}",
                AiTextSanitizer.Sanitize(requested), AiTextSanitizer.Sanitize(ex.Message));
            throw;
        }
    }

    private static string SafeBaseDirectory() => Path.Combine(AppStoragePaths.LocalCache(), "copilot-sdk");

    private static string SafeWorkingDirectory() => AppStoragePaths.LocalCache();

    /// <summary>Result from the CLI-based Copilot path, including requested and actual model names.</summary>
    private sealed record CopilotRunResult(string Content, string RequestedModel, string ActualModel);
}
#pragma warning restore MCPSDK001
