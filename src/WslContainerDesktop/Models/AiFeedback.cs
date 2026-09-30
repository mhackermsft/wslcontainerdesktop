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
    /// <summary>Represents the informational option.</summary>
    Informational,
    /// <summary>Represents the success option.</summary>
    Success,
    /// <summary>Represents the warning option.</summary>
    Warning,
    /// <summary>Represents the error option.</summary>
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

    /// <summary>Gets or sets the severity.</summary>
    public AiFeedbackSeverity Severity { get; init; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets or sets the message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Bounded, redacted diagnostic detail (provider, operation, endpoint, status, response
    /// snippet). Recognized secret fields and credential shapes are masked, not arbitrary secrets.
    /// Null when there is nothing beyond the friendly message worth showing.</summary>
    public string? TechnicalDetails { get; init; }

    /// <summary>True once there is something to show; XAML binds a container's visibility to this
    /// so <see cref="None"/> renders nothing instead of an empty bar.</summary>
    public bool IsVisible => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Message);

    /// <summary>Gets a value indicating whether this value has technical details.</summary>
    public bool HasTechnicalDetails => !string.IsNullOrWhiteSpace(TechnicalDetails);

    /// <summary>Performs the informational helper used by this model or dialog.</summary>
    /// <param name="title">The title value supplied by the caller.</param>
    /// <param name="message">The message value supplied by the caller.</param>
    /// <param name="technicalDetails">The technical details value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public static AiFeedback Informational(string title, string message, string? technicalDetails = null) =>
        new() { Severity = AiFeedbackSeverity.Informational, Title = title, Message = message, TechnicalDetails = technicalDetails };

    /// <summary>Performs the success helper used by this model or dialog.</summary>
    /// <param name="title">The title value supplied by the caller.</param>
    /// <param name="message">The message value supplied by the caller.</param>
    /// <param name="technicalDetails">The technical details value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public static AiFeedback Success(string title, string message, string? technicalDetails = null) =>
        new() { Severity = AiFeedbackSeverity.Success, Title = title, Message = message, TechnicalDetails = technicalDetails };

    /// <summary>Performs the warning helper used by this model or dialog.</summary>
    /// <param name="title">The title value supplied by the caller.</param>
    /// <param name="message">The message value supplied by the caller.</param>
    /// <param name="technicalDetails">The technical details value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public static AiFeedback Warning(string title, string message, string? technicalDetails = null) =>
        new() { Severity = AiFeedbackSeverity.Warning, Title = title, Message = message, TechnicalDetails = technicalDetails };

    /// <summary>Performs the error helper used by this model or dialog.</summary>
    /// <param name="title">The title value supplied by the caller.</param>
    /// <param name="message">The message value supplied by the caller.</param>
    /// <param name="technicalDetails">The technical details value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public static AiFeedback Error(string title, string message, string? technicalDetails = null) =>
        new() { Severity = AiFeedbackSeverity.Error, Title = title, Message = message, TechnicalDetails = technicalDetails };
}
