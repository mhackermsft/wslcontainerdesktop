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

using System.Net;
using System.Text;
using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Tests.Services;

internal sealed class AiContractHarness
{
    public Dictionary<string, object?> SettingsValues { get; } = new()
    {
        [nameof(ISettingsService.AiFeaturesEnabled)] = true,
        [nameof(ISettingsService.AiProvider)] = AiProviderKind.OpenAi,
        [nameof(ISettingsService.AiOpenAiEndpoint)] = "https://provider.invalid/v1",
        [nameof(ISettingsService.AiOpenAiModel)] = "synthetic-model",
        [nameof(ISettingsService.AiAzureOpenAiEndpoint)] = "https://azure.invalid",
        [nameof(ISettingsService.AiAzureOpenAiDeployment)] = "synthetic-deployment",
        [nameof(ISettingsService.AiOllamaEndpoint)] = "http://ollama.invalid:11434",
        [nameof(ISettingsService.AiOllamaModel)] = "synthetic-model",
        [nameof(ISettingsService.AiGitHubCopilotModel)] = "synthetic-model",
    };

    public HashSet<string> AutoApproved { get; } = new(StringComparer.Ordinal);
    public List<ActivityEvent> Activity { get; } = [];
    public List<string> PersistedActivity { get; } = [];
    public ISettingsService Settings { get; }
    public ScriptedProvider Provider { get; } = new();
    public ScriptedTools Tools { get; } = new();
    public ContainerAssistantService Assistant { get; }
    public ObservedCapabilities Capabilities { get; } = new();

    public AiContractHarness(Func<ISettingsService, IAssistantToolset>? toolsetFactory = null,
        Func<ISettingsService, IEnumerable<IAiChatProvider>>? providerFactory = null,
        TimeProvider? timeProvider = null,
        IWslcCapabilitiesService? engineCapabilities = null)
    {
        Settings = NetworkTestProxy.Create<ISettingsService>((method, args) =>
        {
            if (method.Name == nameof(ISettingsService.IsAssistantToolAutoApproved))
            {
                return AutoApproved.Contains((string)args[0]!);
            }

            if (method.Name.StartsWith("get_", StringComparison.Ordinal)
                && SettingsValues.TryGetValue(method.Name[4..], out var value))
            {
                return value;
            }

            throw new InvalidOperationException($"Unconfigured settings call: {method.Name}");
        });
        var activity = NetworkTestProxy.Create<IActivityLog>((method, args) =>
        {
            if (method.Name != nameof(IActivityLog.Record))
            {
                throw new InvalidOperationException($"Unconfigured activity call: {method.Name}");
            }

            var item = (ActivityEvent)args[0]!;
            Activity.Add(item);
            PersistedActivity.Add(JsonSerializer.Serialize(item));
            return null;
        });
        Assistant = new(Settings, providerFactory?.Invoke(Settings) ?? [Provider], toolsetFactory?.Invoke(Settings) ?? Tools,
            new AssistantActionGate(Settings), activity, Capabilities, timeProvider, engineCapabilities);
    }

    public static AiToolCall Call(string name = "stop_container", string arguments = """{"id":"approved-id"}""") =>
        new() { Id = Guid.NewGuid().ToString("N"), Name = name, ArgumentsJson = arguments };

    public static TaskCompletionSource<T> Signal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class ScriptedProvider : IAiChatProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAi;
        public string DisplayName => "Scripted provider";
        public List<IReadOnlyList<AiChatMessage>> Requests { get; } = [];
        public List<AiChatRequest> CapturedRequests { get; } = [];
        public Queue<Func<Func<AiToolCall, CancellationToken, Task<string>>, CancellationToken, Task<string>>> Turns { get; } = new();

        public async Task<AiChatTurnResult> RunTurnAsync(
            AiChatRequest request,
            IReadOnlyList<AiToolDefinition> tools,
            Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
            CancellationToken ct)
        {
            Requests.Add(request.History.ToArray());
            CapturedRequests.Add(request);
            Configurations.Add(request.Configuration);
            var text = await Turns.Dequeue()(invokeToolAsync, ct);
            return new(text, [new() { Role = "assistant", Content = text }]);
        }

        public List<AiChatConfiguration> Configurations { get; } = [];
    }

    internal sealed class ScriptedTools : IAssistantToolset
    {
        public AssistantPermissionCategory Category { get; set; } = AssistantPermissionCategory.Lifecycle;
        public List<AiToolCall> Resolved { get; } = [];
        public List<AiToolCall> Executed { get; } = [];
        public Func<AiToolCall, CancellationToken, Task<string>> Execute { get; set; } =
            (_, _) => Task.FromResult("Stopped approved-id.");
        public Func<AiToolCall, Exception?>? ResolutionFailure { get; set; }
        public Func<AiToolCall, CancellationToken, Task<AssistantResolvedToolCall>>? Resolve { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<AiToolDefinition>>>? Definitions { get; set; }

        public Task<IReadOnlyList<AiToolDefinition>> GetDefinitionsAsync(CancellationToken ct) =>
            Definitions?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<AiToolDefinition>>(
                [new() { Name = "stop_container", Description = "Stop a synthetic container", JsonSchemaParameters = """{"type":"object"}""" }]);

        public Task<AssistantResolvedToolCall> ResolveAsync(AiToolCall call, CancellationToken ct)
        {
            Resolved.Add(call);
            if (Resolve is not null) return Resolve(call, ct);
            if (ResolutionFailure?.Invoke(call) is { } failure)
            {
                throw failure;
            }

            return Task.FromResult(new AssistantResolvedToolCall(
                call, Category, "Stop approved-id", call.ArgumentsJson, token =>
                {
                    Executed.Add(call);
                    return Execute(call, token);
                }));
        }
    }

    internal sealed class Credentials(string? secret = "synthetic-key-not-a-credential") : IAiCredentialStore
    {
        public bool TryReadSecret(AiProviderKind provider, out string? value)
        {
            value = secret;
            return value is not null;
        }

        public void WriteSecret(AiProviderKind provider, string value) =>
            throw new InvalidOperationException("Tests must not persist credentials.");

        public void DeleteSecret(AiProviderKind provider) =>
            throw new InvalidOperationException("Tests must not modify credentials.");
    }

    internal sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        public Queue<Func<CancellationToken, Task<HttpResponseMessage>>> Responses { get; } = new();

        public void Enqueue(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            Responses.Enqueue(_ => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(new(
                request.RequestUri!,
                request.Method,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            if (Responses.Count == 0)
            {
                throw new InvalidOperationException("Unexpected HTTP request; no real network fallback is permitted.");
            }

            return await Responses.Dequeue()(ct);
        }
    }

    internal sealed record CapturedRequest(Uri Uri, HttpMethod Method, string Body, Dictionary<string, string> Headers);

    // Existing orchestration scenarios explicitly begin with positive capability evidence.
    // Capability-layer tests override this; production has no permissive default.
    internal sealed class ObservedCapabilities : IAiCapabilityService
    {
        public AiSupport Chat { get; set; } = AiSupport.Supported;
        public AiSupport Tools { get; set; } = AiSupport.Supported;
        public AiSupport Json { get; set; } = AiSupport.Supported;
        public AiCapabilitySnapshot GetCached(AiChatConfiguration configuration) => new(configuration)
        {
            Chat = new(Chat, AiObservationSource.HarmlessProbe),
            Tools = new(Tools, AiObservationSource.HarmlessProbe),
            StructuredJson = new(Json, AiObservationSource.HarmlessProbe),
            Endpoint = AiEndpointState.Reachable,
        };
        public Task<AiCapabilitySnapshot> GetAsync(AiChatConfiguration configuration, bool probe = false, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(GetCached(configuration));
        }
        public void Invalidate() { Chat = Tools = Json = AiSupport.Unknown; }
    }
}
