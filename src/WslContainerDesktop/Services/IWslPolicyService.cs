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

namespace WslContainerDesktop.Services;

/// <summary>State of the enterprise registry allowlist policy read from Windows policy keys.</summary>
public enum WslRegistryAllowlistState
{
    /// <summary>No registry allowlist is configured; all registries are allowed by this policy.</summary>
    Unrestricted,

    /// <summary>A valid allowlist is configured and should be enforced by callers.</summary>
    Configured,

    /// <summary>The policy value exists but could not be parsed safely.</summary>
    Invalid,
}

/// <summary>Registry allowlist policy plus any diagnostic explaining an invalid value.</summary>
public sealed record WslRegistryAllowlist(
    WslRegistryAllowlistState State,
    IReadOnlyList<string> Registries,
    string? Diagnostic = null)
{
    /// <summary>True when callers should enforce <see cref="Registries"/>.</summary>
    public bool IsConfigured => State == WslRegistryAllowlistState.Configured;
}

/// <summary>Combined WSL policy snapshot used before running container engine operations.</summary>
public sealed record WslPolicySnapshot(
    bool AllowWsl,
    bool AllowWslContainers,
    WslRegistryAllowlist RegistryAllowlist)
{
    /// <summary>True when either base WSL or WSL container policy disables containers.</summary>
    public bool WslContainersDisabled => !AllowWsl || !AllowWslContainers;
}

/// <summary>Reads effective WSL policy for services that must fail closed before invoking <c>wslc.exe</c>.</summary>
public interface IWslPolicyService
{
    /// <summary>Returns the current machine/user policy snapshot.</summary>
    WslPolicySnapshot GetPolicy();
}

/// <summary>Raw registry values before they are interpreted into a policy snapshot.</summary>
public sealed record WslPolicyRegistryData(
    int? AllowWsl,
    int? AllowWslContainer,
    IReadOnlyList<string>? RegistryAllowlist,
    string? RegistryAllowlistDiagnostic);

/// <summary>Small abstraction over Windows registry access so policy parsing can be tested.</summary>
public interface IWslPolicyRegistryReader
{
    /// <summary>Reads the policy values without interpreting allow/deny semantics.</summary>
    WslPolicyRegistryData Read();
}
