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

using System.Security;
using Microsoft.Win32;

namespace WslContainerDesktop.Services;

/// <summary>
/// Reads machine-wide WSL policy values from the Windows registry. The policy service converts
/// missing values into the app's permissive/default policy model.
/// </summary>
public sealed class RegistryWslPolicyRegistryReader : IWslPolicyRegistryReader
{
    private const string PolicyKeyPath = @"Software\Policies\WSL";
    private const string AllowlistKeyName = "WSLContainerRegistryAllowlist";

    /// <inheritdoc/>
    public WslPolicyRegistryData Read()
    {
        using var policy = Registry.LocalMachine.OpenSubKey(PolicyKeyPath);
        return new(
            ReadDword(policy, "AllowWSL"),
            ReadDword(policy, "AllowWSLContainer"),
            ReadAllowlist(out var diagnostic),
            diagnostic);
    }

    private static int? ReadDword(RegistryKey? key, string name)
    {
        if (key is null || key.GetValue(name) is not int value)
        {
            return null;
        }

        return value;
    }

    private static IReadOnlyList<string>? ReadAllowlist(out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            using var allowlist = Registry.LocalMachine.OpenSubKey($@"{PolicyKeyPath}\{AllowlistKeyName}");
            if (allowlist is null)
            {
                return null;
            }

            var values = new List<string>();
            foreach (var name in allowlist.GetValueNames())
            {
                if (allowlist.GetValueKind(name) != RegistryValueKind.String)
                {
                    diagnostic = $"{AllowlistKeyName} contains a non-string registry value.";
                    return null;
                }

                if (allowlist.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }

            return values;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            diagnostic = $"Could not read {AllowlistKeyName}: {ex.Message}";
            return null;
        }
    }
}
