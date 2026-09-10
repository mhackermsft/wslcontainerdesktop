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

public enum ComposeSettingDisposition { Supported, Approximated, Ignored, Blocked }
public enum ComposeExecutionBackend { LegacyRun, NativeCreateConnectStart, Unknown }
public enum ComposePolicyOwner { None, Engine, Application, Unknown }

/// <summary>Display-only, secret-redacted evidence. Never use display strings to authorize work.</summary>
public sealed record ComposeCompatibilitySetting(
    string Service, string Setting, ComposeSettingDisposition Disposition,
    string EffectiveValue, string Explanation, string Source,
    WslcCapabilitySupport? Capability = null)
{
    public string Summary => $"{Service} · {Setting} — {Disposition}" +
        (Capability is { } support ? $" (capability: {support})" : "");
    public string Detail => $"{EffectiveValue}\n{Explanation}\nSource: {Source}";
}

public sealed record ComposeCompatibilityPreview(
    string Project, ComposeLifecycleOperation Operation,
    IReadOnlyList<ComposeCompatibilitySetting> Settings)
{
    public bool CanApply => Settings.All(s => s.Disposition != ComposeSettingDisposition.Blocked);
    public bool HasWarnings => Settings.Any(s => s.Disposition is
        ComposeSettingDisposition.Approximated or ComposeSettingDisposition.Ignored);
    public string Summary => CanApply
        ? "Review the resolved settings before applying. Inventory and capabilities will be checked again."
        : "Deployment blocked. Resolve the blocked settings and review again. Blockers cannot be ignored.";
}
