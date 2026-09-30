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
