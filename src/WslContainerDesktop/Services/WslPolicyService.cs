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

/// <summary>
/// Converts raw Windows registry policy values into the policy snapshot shown and enforced by the app.
/// </summary>
public sealed class WslPolicyService(IWslPolicyRegistryReader registry) : IWslPolicyService
{
    /// <inheritdoc/>
    public WslPolicySnapshot GetPolicy()
    {
        var data = registry.Read();
        var allowlist = data.RegistryAllowlistDiagnostic is not null
            ? new WslRegistryAllowlist(WslRegistryAllowlistState.Invalid, [], data.RegistryAllowlistDiagnostic)
            : (data.RegistryAllowlist ?? [])
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim().TrimEnd('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray() is { Length: > 0 } configured
                    ? new WslRegistryAllowlist(WslRegistryAllowlistState.Configured, configured)
                    : new WslRegistryAllowlist(WslRegistryAllowlistState.Unrestricted, []);

        return new WslPolicySnapshot(IsAllowed(data.AllowWsl), IsAllowed(data.AllowWslContainer), allowlist);
    }

    private static bool IsAllowed(int? value) => value is not 0;
}
