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

using System.Text;
using System.Text.RegularExpressions;

namespace WslContainerDesktop.Services;

/// <summary>
/// Turns a live <c>kubectl get -o yaml</c> manifest into one that is safe to <c>kubectl apply</c>
/// again by stripping server-managed fields. Pure and stateless so it is easy to reason about and
/// unit test in isolation.
/// </summary>
public static class K8sManifestSanitizer
{
    /// <summary>
    /// Strips fields that make a live manifest un-appliable: the top-level <c>status:</c> block,
    /// the <c>metadata.managedFields</c> block, and server-managed identity/version keys
    /// (resourceVersion/uid/creationTimestamp/generation/selfLink).
    /// </summary>
    public static string Sanitize(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var skipStatus = false;
        var managedIndent = -1;

        foreach (var line in lines)
        {
            // status: is emitted last at column 0 — drop it and everything after.
            if (line.StartsWith("status:", StringComparison.Ordinal))
            {
                skipStatus = true;
            }

            if (skipStatus)
            {
                continue;
            }

            // Skip a nested managedFields: block by indentation.
            if (managedIndent >= 0)
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                var indent = line.Length - line.TrimStart().Length;
                if (indent > managedIndent)
                {
                    continue;
                }

                managedIndent = -1;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("managedFields:", StringComparison.Ordinal))
            {
                managedIndent = line.Length - trimmed.Length;
                continue;
            }

            if (Regex.IsMatch(line, @"^\s+(resourceVersion|uid|creationTimestamp|generation|selfLink):"))
            {
                continue;
            }

            sb.Append(line).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }
}
