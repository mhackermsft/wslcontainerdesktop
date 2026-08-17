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
    public static AiErrorContext For(AiProviderKind provider, string operation, string? endpoint = null, string? modelOrDeployment = null) =>
        new(provider, provider.DisplayName(), operation, endpoint, modelOrDeployment);
}

/// <summary>
/// Converts provider/HTTP exceptions into a friendly, actionable <see cref="AiFeedback"/>: a short
/// title and message for the primary UI, plus optional sanitized technical details for an
/// expandable/copyable section. This is the single place that turns "raw exception" into
/// "professional inline feedback" — callers should not hand-roll <c>ex.Message</c> into user-facing
/// text. Never surfaces secrets; see <see cref="AiTextSanitizer"/>.
/// </summary>
public static class AiErrorClassifier
{
    /// <summary>Classifies <paramref name="ex"/> into user-facing feedback. Pass the same
    /// <see cref="CancellationToken"/> used for the failed operation as <paramref name="ct"/> so a
    /// user-initiated cancellation can be distinguished from a provider-side timeout.</summary>
    public static AiFeedback Classify(Exception ex, AiErrorContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ex);

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
                    "Unexpected response",
                    $"{context.ProviderDisplayName} did not return a response in the expected OpenAI, Azure OpenAI, or Ollama shape.",
                    BuildTechnicalDetails(ex, context, null, null));

            case InvalidOperationException:
                // Our own pre-flight validation messages (missing model/key/endpoint, bad base
                // URL) are already specific and safe to show directly — no secrets, no raw HTTP
                // bodies, just "here is exactly what to fix in Settings".
                return AiFeedback.Warning("Configuration needed", ex.Message);

            default:
                return AiFeedback.Error(
                    $"{context.Operation} failed",
                    "An unexpected error occurred.",
                    BuildTechnicalDetails(ex, context, null, null));
        }
    }

    /// <summary>Feedback for an operation the user explicitly canceled (e.g. clicked Stop).</summary>
    public static AiFeedback Canceled(AiErrorContext context) =>
        AiFeedback.Informational($"{context.Operation} canceled", "Canceled.");

    private static AiFeedback ClassifyCancellation(AiErrorContext context, CancellationToken ct)
    {
        if (ct.CanBeCanceled && ct.IsCancellationRequested)
        {
            return Canceled(context);
        }

        // The exception's own token was canceled (an internal timeout), not the caller's — the
        // request took too long rather than being canceled by the user.
        return AiFeedback.Warning(
            $"{context.Operation} timed out",
            "The request took too long to complete. Check that the endpoint is reachable, then try again.");
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
            AiFailureKind.Configuration => AiFeedback.Warning("Configuration needed", ex.Message, details),
            AiFailureKind.Authentication => AuthenticationFeedback(effective, ex.StatusCode, details),
            AiFailureKind.NotFound => AiFeedback.Error("Endpoint not found", NotFoundMessage(effective), details),
            AiFailureKind.RateLimited => AiFeedback.Warning(
                "Rate limited",
                $"{effective.ProviderDisplayName} is throttling requests. Wait a moment, check your plan or quota, or try again later.",
                details),
            AiFailureKind.ServerError => AiFeedback.Error(
                "Provider server error",
                $"{effective.ProviderDisplayName} reported a server-side error. Try again shortly.",
                details),
            _ => AiFeedback.Error($"{effective.Operation} failed", ex.Message, details),
        };
    }

    private static AiFeedback AuthenticationFeedback(AiErrorContext context, int? statusCode, string details)
    {
        var title = statusCode == 403 ? "Authentication failed" : "Authentication required";
        var message = context.Provider switch
        {
            AiProviderKind.GitHubCopilot =>
                "Sign in to the GitHub Copilot CLI (`copilot login`) and confirm your account has Copilot entitlement, then try again.",
            AiProviderKind.AzureOpenAi => "Save a valid Azure OpenAI API key in Settings, then try again.",
            AiProviderKind.OpenAi =>
                "Save a valid API key in Settings — hosted endpoints such as api.openai.com require one — then try again.",
            AiProviderKind.Ollama =>
                "Ollama does not use an API key. If this endpoint sits behind a proxy that requires authentication, adjust the proxy or point at a direct Ollama endpoint.",
            _ => "Check the saved credential for the selected provider in Settings.",
        };
        return AiFeedback.Error(title, message, details);
    }

    private static string NotFoundMessage(AiErrorContext context) => context.Provider switch
    {
        AiProviderKind.OpenAi => "The endpoint did not recognize this route. Check the base URL and version path (for example /v1).",
        AiProviderKind.AzureOpenAi => "Check the Azure OpenAI endpoint and deployment name in Settings — the deployment may not exist or may be misspelled.",
        AiProviderKind.Ollama => "Check the Ollama base URL in Settings.",
        AiProviderKind.GitHubCopilot => "The requested model was not found. Choose an available model in Settings.",
        _ => "Check the endpoint configured in Settings.",
    };

    private static AiFeedback ClassifyHttpRequestException(HttpRequestException ex, AiErrorContext context)
    {
        var statusCode = ex.StatusCode is { } sc ? (int)sc : (int?)null;
        var details = BuildTechnicalDetails(ex, context, statusCode, null);
        var endpointText = string.IsNullOrWhiteSpace(context.Endpoint) ? "the configured endpoint" : context.Endpoint;
        var (title, message) = ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError =>
                ("Endpoint not found", $"Could not resolve the hostname for {endpointText}. Check the base URL."),
            HttpRequestError.ConnectionError =>
                ("Connection failed", $"Could not connect to {endpointText}. Make sure the server is running and reachable."),
            HttpRequestError.SecureConnectionError =>
                ("Connection not secure", "TLS/certificate validation failed. Check the endpoint's certificate, or use http instead of https for a local server."),
            HttpRequestError.ResponseEnded or HttpRequestError.InvalidResponse =>
                ("Connection dropped", "The connection closed before a valid response was received. Try again."),
            _ => ("Connection failed", $"Could not reach {endpointText}. Check the endpoint and that the server is running."),
        };

        return AiFeedback.Error(title, message, details);
    }

    private static string BuildTechnicalDetails(Exception ex, AiErrorContext context, int? statusCode, string? responseDetail)
    {
        var lines = new List<string>
        {
            $"Provider: {context.ProviderDisplayName}",
            $"Operation: {context.Operation}",
        };

        if (!string.IsNullOrWhiteSpace(context.Endpoint))
        {
            lines.Add($"Endpoint: {context.Endpoint}");
        }

        if (!string.IsNullOrWhiteSpace(context.ModelOrDeployment))
        {
            lines.Add($"Model/Deployment: {context.ModelOrDeployment}");
        }

        if (statusCode is { } code)
        {
            lines.Add($"HTTP status: {code}");
        }

        if (!string.IsNullOrWhiteSpace(responseDetail))
        {
            lines.Add($"Response: {responseDetail}");
        }

        lines.Add($"Exception: {ex.GetType().Name}: {AiTextSanitizer.Truncate(AiTextSanitizer.Redact(ex.Message), 400)}");
        return string.Join(Environment.NewLine, lines);
    }
}
