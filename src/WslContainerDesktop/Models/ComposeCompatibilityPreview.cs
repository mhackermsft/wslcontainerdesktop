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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using WslContainerDesktop.Services;

namespace WslContainerDesktop.Models;

/// <summary>Values that describe compose setting disposition states or choices in WSL Container Desktop workflows.</summary>
public enum ComposeSettingDisposition { Supported, Approximated, Ignored, Blocked }
/// <summary>Values that describe compose execution backend states or choices in WSL Container Desktop workflows.</summary>
public enum ComposeExecutionBackend { LegacyRun, NativeCreateConnectStart, Unknown }
/// <summary>Values that describe compose policy owner states or choices in WSL Container Desktop workflows.</summary>
public enum ComposePolicyOwner { None, Engine, Application, Unknown }

/// <summary>Display-only, secret-redacted evidence. Never use display strings to authorize work.</summary>
public sealed record ComposeCompatibilitySetting(
    string Service, string Setting, ComposeSettingDisposition Disposition,
    string EffectiveValue, string Explanation, string Source,
    WslcCapabilitySupport? Capability = null)
{
    /// <summary>Returns the one-line summary so list rows announce it to screen readers instead of the type.</summary>
    public override string ToString() => Summary;

    /// <summary>Gets the summary.</summary>
    public string Summary => $"{Service} · {Setting} — {Disposition}" +
        (Capability is { } support ? $" (capability: {support})" : "");
    /// <summary>Gets the detail.</summary>
    public string Detail => $"{EffectiveValue}\n{Explanation}\nSource: {Source}";

    /// <summary>Localized labels for review, retaining raw service and setting identifiers.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplaySummary => UiText.Get("Root_Audit_Compose_Summary", "{0} · {1} — {2}{3}",
        Service, Setting, UiText.Translate(Disposition.ToString()),
        Capability is { } support ? UiText.Get("Root_Audit_Compose_Capability", " (capability: {0})",
            UiText.Translate(support.ToString())) : "");

    /// <summary>Localized application guidance; execution evidence stays in the raw properties.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayDetail => string.Join("\n", DisplayEffectiveValue,
        UiText.TranslateLines(Explanation), UiText.Get("Root_Audit_Compose_Source", "Source: {0}", UiText.Translate(Source)));

    private string DisplayEffectiveValue
    {
        get
        {
            // These values are supplied by the user or are literal execution settings.
            if (Setting is "image" or "replicas" or "profiles" or "ports" or "volumes" or "network_mode" or "depends_on" or "extra_hosts")
                return EffectiveValue;
            if (Setting == "instances")
                return string.Join("\n", EffectiveValue.Split('\n').Select(line =>
                {
                    var parts = line.Split(" · ", 4, StringSplitOptions.None);
                    return parts.Length == 4
                        ? UiText.Get("Root_Audit_Compose_Instance", "{0} · {1} · {2} · {3}",
                            parts[0], parts[1], UiText.Translate(parts[2]), UiText.TranslateLines(parts[3]))
                        : UiText.TranslateLines(line);
                }));
            if (Setting == "healthcheck" && EffectiveValue.StartsWith("Probe owner: ", StringComparison.Ordinal) &&
                EffectiveValue.EndsWith("; auto-heal owner: application", StringComparison.Ordinal))
                return UiText.Get("Root_Audit_Compose_ProbeOwner", "Probe owner: {0}; auto-heal owner: application",
                    UiText.Translate(EffectiveValue["Probe owner: ".Length..^"; auto-heal owner: application".Length]));
            if (Setting == "restart" && EffectiveValue.Split("; owner: ", 2, StringSplitOptions.None) is { Length: 2 } owners)
                return UiText.Get("Root_Audit_Compose_RestartOwner", "{0}; owner: {1}",
                    UiText.Translate(owners[0]), UiText.Translate(owners[1]));
            return UiText.TranslateLines(EffectiveValue);
        }
    }
}

/// <summary>Immutable or init-only data model that carries compose compatibility preview information between services and view models.</summary>
public sealed record ComposeCompatibilityPreview(
    string Project, ComposeLifecycleOperation Operation,
    IReadOnlyList<ComposeCompatibilitySetting> Settings)
{
    /// <summary>Gets a value indicating whether this value can apply.</summary>
    public bool CanApply => Settings.All(s => s.Disposition != ComposeSettingDisposition.Blocked);
    /// <summary>Gets a value indicating whether this value has warnings.</summary>
    public bool HasWarnings => Settings.Any(s => s.Disposition is
        ComposeSettingDisposition.Approximated or ComposeSettingDisposition.Ignored);
    /// <summary>Gets the summary.</summary>
    public string Summary => CanApply
        ? "Review the resolved settings before applying. Inventory and capabilities will be checked again."
        : "Deployment blocked. Resolve the blocked settings and review again. Blockers cannot be ignored.";
}
