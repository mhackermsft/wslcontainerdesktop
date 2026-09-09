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

using System.Diagnostics;
using System.Text;

namespace WslContainerDesktop.Services;

/// <summary>Native cp needs a seekable stdin handle to determine the archive's Content-Length.</summary>
internal static class WslcCopyInput
{
    public static ProcessStartInfo CreateStartInfo(
        string executable, IEnumerable<string> arguments, string archive)
    {
        var args = arguments.ToArray();
        if (args.Length != 4 || args[0] != "container" || args[1] != "cp" || args[2] != "-")
        {
            throw new ArgumentException("File-backed input is only supported for container cp.");
        }
        var values = new[] { executable, args[3], archive };
        if (values.Any(value => string.IsNullOrEmpty(value) || value.IndexOfAny(['"', '\r', '\n', '\0']) >= 0) ||
            values.Sum(value => value.Length) > 7500)
        {
            throw new ArgumentException("The native copy executable, target or staging path cannot be safely redirected.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // The command is fixed. Quoted environment expansions are data, never re-expanded
        // (no CALL, AutoRun or delayed expansion). Reject quote/newline delimiters above.
        // Unlike RedirectStandardInput's anonymous pipe, '<' gives WSLC a real file handle.
        // cmd.exe does not use CRT argv quoting, so ArgumentList would escape these fixed
        // quotes incorrectly. No caller data is concatenated into this command line.
        psi.Arguments = "/d /v:off /s /c \"\"%WCD_CP_EXE%\" container cp - \"%WCD_CP_TARGET%\" < \"%WCD_CP_ARCHIVE%\"\"";
        psi.Environment["WCD_CP_EXE"] = executable;
        psi.Environment["WCD_CP_TARGET"] = args[3];
        psi.Environment["WCD_CP_ARCHIVE"] = archive;
        return psi;
    }
}
