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

using System.Globalization;
using System.Text;

namespace WslContainerDesktop.Models;

/// <summary>The immutable Windows working directory and scripts for one start or rebuild.</summary>
public sealed class DevContainerHostCommandReview
{
    internal DevContainerHostCommandReview(string workspacePath, IEnumerable<string> commands)
    {
        Commands = Array.AsReadOnly(commands.Where(c => !string.IsNullOrWhiteSpace(c)).ToArray());
        // Resolve relative paths before review; never resolve a different working directory later.
        WorkspacePath = Commands.Count == 0 ? workspacePath : ResolveWorkspace(workspacePath);
        DisplayWorkspacePath = EscapeForDisplay(WorkspacePath);
        DisplayCommands = Array.AsReadOnly(Commands.Select(EscapeForDisplay).ToArray());
    }

    // Only these original values may be used for execution.
    public string WorkspacePath { get; }
    public IReadOnlyList<string> Commands { get; }

    /// <summary>Display-only escaping prevents invisible characters from concealing the reviewed inputs.</summary>
    public string DisplayWorkspacePath { get; }
    public IReadOnlyList<string> DisplayCommands { get; }

    private static string ResolveWorkspace(string workspacePath)
    {
        var fullPath = Path.GetFullPath(workspacePath);
        // CMD can silently replace UNC/device/extended-length current directories with C:\Windows.
        if (fullPath.Length >= 260 || fullPath.Length < 3 || !char.IsAsciiLetter(fullPath[0]) ||
            fullPath[1] != ':' || fullPath[2] != '\\')
        {
            throw new ArgumentException("Host initializeCommand requires a conventional drive-letter workspace path shorter than 260 characters. " +
                "UNC, device and extended-length workspace paths are not supported by cmd.exe.");
        }
        return fullPath;
    }

    internal static string EscapeForDisplay(string text)
    {
        var display = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            // Keep ordinary multiline scripts readable, but expose standalone CR and all other controls.
            if (character == '\n' || character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                display.Append(character);
                continue;
            }
            if (character == '\\')
            {
                // Distinguish a literal "\u202E" in the input from an escaped U+202E character.
                display.Append(@"\\");
                continue;
            }

            var isPair = char.IsSurrogatePair(text, index);
            var category = CharUnicodeInfo.GetUnicodeCategory(text, index);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                var codePoint = isPair ? char.ConvertToUtf32(text, index) : character;
                display.Append(isPair ? @"\U" : @"\u");
                display.Append(codePoint.ToString(isPair ? "X8" : "X4", CultureInfo.InvariantCulture));
            }
            else
            {
                display.Append(text, index, isPair ? 2 : 1);
            }
            if (isPair) index++;
        }
        return display.ToString();
    }
}
