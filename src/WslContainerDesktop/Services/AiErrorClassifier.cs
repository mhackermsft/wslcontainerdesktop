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

using System.Net.Http;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Identifies the provider/operation/endpoint an AI failure occurred in, so
/// <see cref="AiErrorClassifier"/> can produce provider-specific guidance and a copyable technical
/// detail block. <see cref="Endpoint"/>/<see cref="ModelOrDeployment"/> are optional — when the
/// exception is an <see cref="AiProviderException"/> its own values are used if the context does
/// not supply them.
/// </summary>
public sealed record AiErrorContext(
    AiProviderKind Provider,
    string ProviderDisplayName,
    string Operation,
    string? Endpoint = null,
    string? ModelOrDeployment = null)
{
    /// <summary>
    /// Creates a sanitized context for an operation using the provider's display name.
    /// </summary>
    public static AiErrorContext For(AiProviderKind provider, string operation, string? endpoint = null, string? modelOrDeployment = null) =>
        new(provider, provider.DisplayName(), operation, endpoint, modelOrDeployment);
}

/// <summary>
/// Converts provider/HTTP exceptions into a friendly, actionable <see cref="AiFeedback"/>: a short
/// title and message for the primary UI, plus optional sanitized technical details for an
/// expandable/copyable section. This is the single place that turns "raw exception" into
/// "professional inline feedback" — callers should not hand-roll <c>ex.Message</c> into user-facing
/// text. Masks recognized secret shapes; see <see cref="AiTextSanitizer"/> for limitations.
/// </summary>
public static class AiErrorClassifier
{
    /// <summary>Classifies <paramref name="ex"/> into user-facing feedback. Pass the same
    /// <see cref="CancellationToken"/> used for the failed operation as <paramref name="ct"/> so a
    /// user-initiated cancellation can be distinguished from a provider-side timeout.</summary>
    public static AiFeedback Classify(Exception ex, AiErrorContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ex);
        context = context with
        {
            ProviderDisplayName = AiTextSanitizer.Sanitize(context.ProviderDisplayName),
            Operation = AiTextSanitizer.Sanitize(context.Operation),
            Endpoint = context.Endpoint is null ? null : AiTextSanitizer.Sanitize(context.Endpoint),
            ModelOrDeployment = context.ModelOrDeployment is null ? null : AiTextSanitizer.Sanitize(context.ModelOrDeployment),
        };

        switch (ex)
        {
            case OperationCanceledException:
                return ClassifyCancellation(context, ct);

            case AiProviderException providerEx:
                return ClassifyProviderException(providerEx, context);

            case HttpRequestException httpEx:
                return ClassifyHttpRequestException(httpEx, context);

            case JsonException:
                return AiFeedback.Error(
                    UiText.Get("Common_Text0440", "Unexpected response"),
                    UiText.Get("Common_Text0441", "{0} did not return a response in the expected OpenAI, Azure OpenAI, or Ollama shape.", context.ProviderDisplayName),
                    BuildTechnicalDetails(ex, context, null, null));

            // Checked before InvalidOperationException, which it derives from: the assistant chose
            // an argument shape the tool does not accept, so there is nothing for the user to fix.
            case AssistantArgumentException:
                return AiFeedback.Warning(
                    UiText.Get("Common_Text0442", "The assistant sent an invalid request"),
                    UiText.TranslateLines(AiTextSanitizer.Sanitize(ex.Message)) +
                    UiText.Get("Common_Text0443", " Nothing was run. Ask it to try again; it is told which fields the tool accepts."));

            // Also before InvalidOperationException. The limit is a safety stop doing its job, not
            // a configuration fault; the usual cause is the model retrying a call that keeps
            // failing the same way.
            case AssistantIterationLimitException:
                return AiFeedback.Warning(
                    UiText.Get("Common_Text0444", "The assistant did not finish"),
                    UiText.TranslateLines(AiTextSanitizer.Sanitize(ex.Message)) +
                    UiText.Get("Common_Text0445", " Any tool calls it already completed still happened and were recorded; review ") +
                    UiText.Get("Common_Text0446", "them before retrying, and try narrowing the request into a single step."));

            case InvalidOperationException:
                return AiFeedback.Warning(UiText.Get("Common_Text0447", "Configuration needed"), UiText.TranslateLines(AiTextSanitizer.Sanitize(ex.Message)));

            default:
                return AiFeedback.Error(
                    UiText.Get("Common_Text0448", "{0} failed", context.Operation),
                    UiText.Get("Common_Text0449", "An unexpected error occurred."),
                    BuildTechnicalDetails(ex, context, null, null));
        }
    }

    /// <summary>Feedback for an operation the user explicitly canceled (e.g. clicked Stop).</summary>
    public static AiFeedback Canceled(AiErrorContext context) =>
        AiFeedback.Informational(UiText.Get("Common_Text0450", "{0} canceled", UiText.Translate(context.Operation)), UiText.Get("Common_Canceled", "Canceled."));

    private static AiFeedback ClassifyCancellation(AiErrorContext context, CancellationToken ct)
    {
        if (ct.CanBeCanceled && ct.IsCancellationRequested)
        {
            return Canceled(context);
        }

        // The exception's own token was canceled (an internal timeout), not the caller's — the
        // request took too long rather than being canceled by the user.
        return AiFeedback.Warning(
            UiText.Get("Common_Text0451", "{0} timed out", context.Operation),
            UiText.Get("Common_Text0452", "The request took too long to complete. Check that the endpoint is reachable, then try again."));
    }

    private static AiFeedback ClassifyProviderException(AiProviderException ex, AiErrorContext context)
    {
        var effective = context with
        {
            Endpoint = context.Endpoint ?? ex.Endpoint,
            ModelOrDeployment = context.ModelOrDeployment ?? ex.ModelOrDeployment,
        };
        var details = BuildTechnicalDetails(ex, effective, ex.StatusCode, ex.ResponseDetail);

        return ex.Kind switch
        {
            AiFailureKind.Configuration => AiFeedback.Warning(UiText.Get("Common_Text0447", "Configuration needed"), UiText.TranslateLines(ex.Message), details),
            AiFailureKind.Authentication => AuthenticationFeedback(effective, ex.StatusCode, details),
            AiFailureKind.NotFound => AiFeedback.Error(UiText.Get("Common_Text0453", "Endpoint not found"), NotFoundMessage(effective), details),
            AiFailureKind.RateLimited => AiFeedback.Warning(
                UiText.Get("Common_Text0454", "Rate limited"),
                UiText.Get("Common_Text0455", "{0} is throttling requests. Wait a moment, check your plan or quota, or try again later.", effective.ProviderDisplayName),
                details),
            AiFailureKind.ServerError => AiFeedback.Error(
                UiText.Get("Common_Text0456", "Provider server error"),
                UiText.Get("Common_Text0457", "{0} reported a server-side error. Try again shortly.", effective.ProviderDisplayName),
                details),
            _ => AiFeedback.Error(UiText.Get("Common_Text0448", "{0} failed", UiText.Translate(effective.Operation)), UiText.TranslateLines(ex.Message), details),
        };
    }

    private static AiFeedback AuthenticationFeedback(AiErrorContext context, int? statusCode, string details)
    {
        var title = statusCode == 403 ? UiText.Get("Common_Text0458", "Authentication failed") : UiText.Get("Common_Text0459", "Authentication required");
        var message = context.Provider switch
        {
            AiProviderKind.GitHubCopilot =>
                UiText.Get("Common_Text0460", "Sign in to the GitHub Copilot CLI (`copilot login`) and confirm your account has Copilot entitlement, then try again."),
            AiProviderKind.AzureOpenAi => UiText.Get("Common_Text0461", "Save a valid Azure OpenAI API key in Settings, then try again."),
            AiProviderKind.OpenAi =>
                UiText.Get("Common_Text0462", "Save a valid API key in Settings — hosted endpoints such as api.openai.com require one — then try again."),
            AiProviderKind.Ollama =>
                UiText.Get("Common_Text0463", "Ollama does not use an API key. If this endpoint sits behind a proxy that requires authentication, adjust the proxy or point at a direct Ollama endpoint."),
            _ => UiText.Get("Common_Text0464", "Check the saved credential for the selected provider in Settings."),
        };
        return AiFeedback.Error(title, message, details);
    }

    private static string NotFoundMessage(AiErrorContext context) => context.Provider switch
    {
        AiProviderKind.OpenAi => UiText.Get("Common_Text0465", "The endpoint did not recognize this route. Check the base URL and version path (for example /v1)."),
        AiProviderKind.AzureOpenAi => UiText.Get("Common_Text0466", "Check the Azure OpenAI endpoint and deployment name in Settings — the deployment may not exist or may be misspelled."),
        AiProviderKind.Ollama => UiText.Get("Common_Text0467", "Check the Ollama base URL in Settings."),
        AiProviderKind.GitHubCopilot => UiText.Get("Common_Text0468", "The requested model was not found. Choose an available model in Settings."),
        _ => UiText.Get("Common_Text0469", "Check the endpoint configured in Settings."),
    };

    private static AiFeedback ClassifyHttpRequestException(HttpRequestException ex, AiErrorContext context)
    {
        var statusCode = ex.StatusCode is { } sc ? (int)sc : (int?)null;
        var details = BuildTechnicalDetails(ex, context, statusCode, null);
        var endpointText = string.IsNullOrWhiteSpace(context.Endpoint) ? UiText.Get("Common_Text0470", "the configured endpoint") : context.Endpoint;
        var (title, message) = ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError =>
                (UiText.Get("Common_Text0453", "Endpoint not found"), UiText.Get("Common_Text0471", "Could not resolve the hostname for {0}. Check the base URL.", endpointText)),
            HttpRequestError.ConnectionError =>
                (UiText.Get("Common_Text0054", "Connection failed"), UiText.Get("Common_Text0472", "Could not connect to {0}. Make sure the server is running and reachable.", endpointText)),
            HttpRequestError.SecureConnectionError =>
                (UiText.Get("Common_Text0473", "Connection not secure"), UiText.Get("Common_Text0474", "TLS/certificate validation failed. Check the endpoint's certificate, or use http instead of https for a local server.")),
            HttpRequestError.ResponseEnded or HttpRequestError.InvalidResponse =>
                (UiText.Get("Common_Text0475", "Connection dropped"), UiText.Get("Common_Text0476", "The connection closed before a valid response was received. Try again.")),
            _ => (UiText.Get("Common_Text0054", "Connection failed"), UiText.Get("Common_Text0477", "Could not reach {0}. Check the endpoint and that the server is running.", endpointText)),
        };

        return AiFeedback.Error(title, message, details);
    }

    private static string BuildTechnicalDetails(Exception ex, AiErrorContext context, int? statusCode, string? responseDetail)
    {
        var lines = new List<string>
        {
            UiText.Get("Common_Text0478", "Provider: {0}", context.ProviderDisplayName),
            UiText.Get("Common_Text0479", "Operation: {0}", context.Operation),
        };

        if (!string.IsNullOrWhiteSpace(context.Endpoint))
        {
            lines.Add(UiText.Get("Common_Text0480", "Endpoint: {0}", context.Endpoint));
        }

        if (!string.IsNullOrWhiteSpace(context.ModelOrDeployment))
        {
            lines.Add(UiText.Get("Common_Text0481", "Model/Deployment: {0}", context.ModelOrDeployment));
        }

        if (statusCode is { } code)
        {
            lines.Add(UiText.Get("Common_Text0482", "HTTP status: {0}", code));
        }

        if (!string.IsNullOrWhiteSpace(responseDetail))
        {
            lines.Add(UiText.Get("Common_Text0483", "Response: {0}", responseDetail));
        }

        lines.Add(UiText.Get("Common_Text0484", "Exception: {0}: {1}", ex.GetType().Name, AiTextSanitizer.Sanitize(ex.Message, 400)));
        return AiTextSanitizer.Sanitize(string.Join(Environment.NewLine, lines));
    }
}
