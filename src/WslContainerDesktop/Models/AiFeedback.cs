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

/// <summary>
/// Presentation-neutral severity for <see cref="AiFeedback"/>. Views map this to their own control
/// vocabulary (e.g. WinUI <c>InfoBarSeverity</c>) — this type has no dependency on WinUI.
/// </summary>
public enum AiFeedbackSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// A single piece of AI-operation feedback (progress, success, validation, or failure) meant to be
/// rendered inline at the point the operation occurred — typically with a WinUI <c>InfoBar</c>.
/// Keeps the user-facing <see cref="Message"/> short and friendly; any raw provider/HTTP detail
/// belongs in <see cref="TechnicalDetails"/>, which is already bounded and redacted by the time it
/// reaches here (see <c>AiErrorClassifier</c>/<c>AiTextSanitizer</c>) and is safe to display behind
/// an expander and copy to the clipboard.
/// </summary>
public sealed class AiFeedback
{
    /// <summary>A feedback value that renders nothing — the default until an operation runs.</summary>
    public static readonly AiFeedback None = new();

    public AiFeedbackSeverity Severity { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    /// <summary>Bounded, redacted diagnostic detail (provider, operation, endpoint, status, response
    /// snippet). Never contains API keys, tokens, or Authorization headers. Null when there is
    /// nothing beyond the friendly message worth showing.</summary>
    public string? TechnicalDetails { get; init; }

    /// <summary>True once there is something to show; XAML binds a container's visibility to this
    /// so <see cref="None"/> renders nothing instead of an empty bar.</summary>
    public bool IsVisible => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Message);

    public bool HasTechnicalDetails => !string.IsNullOrWhiteSpace(TechnicalDetails);

    public static AiFeedback Informational(string title, string message, string? technicalDetails = null) =>
        new() { Severity = AiFeedbackSeverity.Informational, Title = title, Message = message, TechnicalDetails = technicalDetails };

    public static AiFeedback Success(string title, string message, string? technicalDetails = null) =>
        new() { Severity = AiFeedbackSeverity.Success, Title = title, Message = message, TechnicalDetails = technicalDetails };

    public static AiFeedback Warning(string title, string message, string? technicalDetails = null) =>
        new() { Severity = AiFeedbackSeverity.Warning, Title = title, Message = message, TechnicalDetails = technicalDetails };

    public static AiFeedback Error(string title, string message, string? technicalDetails = null) =>
        new() { Severity = AiFeedbackSeverity.Error, Title = title, Message = message, TechnicalDetails = technicalDetails };
}
