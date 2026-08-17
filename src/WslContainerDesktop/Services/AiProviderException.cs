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
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Broad classification of an <see cref="AiProviderException"/>, used by
/// <see cref="AiErrorClassifier"/> to pick friendly title/message wording without re-parsing
/// exception text.</summary>
public enum AiFailureKind
{
    /// <summary>An unclassified provider failure; the exception's own message is shown as-is.</summary>
    Unexpected,

    /// <summary>A required setting is missing or invalid (model, key, endpoint, deployment). The
    /// exception's message already names the exact setting to fix, so it is shown as-is.</summary>
    Configuration,

    /// <summary>HTTP 401/403, or an equivalent provider-reported sign-in/entitlement failure.</summary>
    Authentication,

    /// <summary>HTTP 404, or an equivalent "route/deployment/model not found" failure.</summary>
    NotFound,

    /// <summary>HTTP 429 (rate limit or quota exceeded).</summary>
    RateLimited,

    /// <summary>HTTP 5xx (provider-side server error).</summary>
    ServerError,
}

/// <summary>
/// Structured failure raised by an AI provider or its pre-flight configuration validation. Carries
/// enough context (provider, operation, HTTP status, endpoint/model, and a bounded/redacted
/// response snippet) for <see cref="AiErrorClassifier"/> to build a friendly, actionable
/// <see cref="AiFeedback"/> without re-parsing prose. <see cref="ResponseDetail"/> is always
/// truncated and redacted by the constructor — never pass raw Authorization headers or API keys
/// into it.
/// </summary>
public sealed class AiProviderException : Exception
{
    public AiProviderException(
        AiProviderKind provider,
        string operation,
        string message,
        AiFailureKind kind,
        int? statusCode = null,
        string? endpoint = null,
        string? modelOrDeployment = null,
        string? responseDetail = null,
        Exception? inner = null)
        : base(message, inner)
    {
        Provider = provider;
        Operation = operation;
        Kind = kind;
        StatusCode = statusCode;
        Endpoint = endpoint;
        ModelOrDeployment = modelOrDeployment;
        ResponseDetail = string.IsNullOrWhiteSpace(responseDetail)
            ? null
            : AiTextSanitizer.Truncate(AiTextSanitizer.Redact(responseDetail), 400);
    }

    public AiProviderKind Provider { get; }

    /// <summary>Short, friendly verb phrase for the attempted operation (e.g. "Refresh models",
    /// "Provider test", "Assistant chat"), used to build titles and technical details.</summary>
    public string Operation { get; }

    public AiFailureKind Kind { get; }

    public int? StatusCode { get; }

    public string? Endpoint { get; }

    public string? ModelOrDeployment { get; }

    /// <summary>Bounded, redacted response body or diagnostic detail. Never contains secrets.</summary>
    public string? ResponseDetail { get; }

    /// <summary>Builds an exception for a non-success HTTP response, classifying <see cref="Kind"/>
    /// from the status code so callers do not need to parse status text themselves.</summary>
    public static AiProviderException FromHttpFailure(
        AiProviderKind provider,
        string operation,
        HttpStatusCode statusCode,
        string? endpoint,
        string? modelOrDeployment,
        string responseBody)
    {
        var code = (int)statusCode;
        var kind = code switch
        {
            401 or 403 => AiFailureKind.Authentication,
            404 => AiFailureKind.NotFound,
            429 => AiFailureKind.RateLimited,
            >= 500 => AiFailureKind.ServerError,
            _ => AiFailureKind.Unexpected,
        };

        return new AiProviderException(
            provider,
            operation,
            $"{provider.DisplayName()} request failed ({code} {statusCode}).",
            kind,
            code,
            endpoint,
            modelOrDeployment,
            responseBody);
    }
}
