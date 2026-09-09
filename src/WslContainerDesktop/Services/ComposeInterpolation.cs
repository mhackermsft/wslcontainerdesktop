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

using System.Text;

namespace WslContainerDesktop.Services;

internal static class ComposeInterpolation
{
    public static string Expand(string text, IReadOnlyDictionary<string, string>? environment,
        string location = "Compose value", Action<string>? warning = null)
    {
        var offset = 0;
        var value = Read(text, ref offset, environment, location, warning, nested: false, evaluate: true, depth: 0);
        if (value.Length > 2_000_000)
            throw Error(location, "Expanded value exceeds the 2,000,000-character configuration limit.");
        return value;
    }

    private static string Read(string text, ref int offset, IReadOnlyDictionary<string, string>? env,
        string location, Action<string>? warning, bool nested, bool evaluate, int depth)
    {
        if (depth > 64)
            throw Error(location, "Interpolation nesting exceeds 64 levels.");
        var output = new StringBuilder();
        var literalBraces = 0;
        while (offset < text.Length)
        {
            if (output.Length > 2_000_000)
                throw Error(location, "Expanded value exceeds the 2,000,000-character configuration limit.");
            var c = text[offset++];
            if (c == '}')
            {
                if (literalBraces > 0) literalBraces--;
                else if (nested) return output.ToString();
            }
            else if (c == '{')
                literalBraces++;
            if (c != '$')
            {
                if (evaluate) output.Append(c);
                continue;
            }
            if (offset < text.Length && text[offset] == '$')
            {
                offset++;
                if (evaluate) output.Append('$');
                continue;
            }
            var braced = offset < text.Length && text[offset] == '{';
            if (braced) offset++;
            if (offset >= text.Length || !IsStart(text[offset]))
            {
                if (braced) throw Error(location, "Malformed interpolation variable. Use ${NAME} or escape a literal dollar as $$.");
                if (evaluate) output.Append('$');
                continue;
            }
            var start = offset++;
            while (offset < text.Length && (IsStart(text[offset]) || char.IsAsciiDigit(text[offset]))) offset++;
            var name = text[start..offset];
            if (name.Length > 128)
                throw Error(location, "Interpolation variable name exceeds 128 characters.");
            string? value = null;
            var set = env?.TryGetValue(name, out value) == true;
            if (!braced)
            {
                if (evaluate && set) output.Append(value);
                else if (evaluate) warning?.Invoke($"{location}: Variable '{name}' is unset; using an empty string.");
                continue;
            }
            if (offset >= text.Length) throw Error(location, "Unclosed interpolation expression. Add the closing }.");
            if (text[offset] == '}')
            {
                offset++;
                if (evaluate && set) output.Append(value);
                else if (evaluate) warning?.Invoke($"{location}: Variable '{name}' is unset; using an empty string.");
                continue;
            }
            var colon = text[offset] == ':';
            if (colon) offset++;
            if (offset >= text.Length || text[offset] is not ('-' or '+' or '?'))
                throw Error(location, "Unsupported interpolation operator. Use -, :-, +, :+, ? or :?.");
            var op = text[offset++];
            var present = set && (!colon || !string.IsNullOrEmpty(value));
            var useOperand = op == '+' ? present : !present;
            // Validate even an unused branch, but don't resolve required expressions in it.
            var operand = Read(text, ref offset, env, location, warning, nested: true,
                evaluate: evaluate && useOperand && op != '?', depth + 1);
            if (!evaluate) continue;
            if (op == '?' && !present)
                throw Error(location, $"Required variable '{name}' is {(colon ? "unset or empty" : "unset")}. " +
                    "Set it in the import environment, process environment or project .env file. " +
                    "The supplied error text is omitted to protect secrets.");
            output.Append(op == '+' ? (present ? operand : "") : (present ? value : operand));
            if (output.Length > 2_000_000)
                throw Error(location, "Expanded value exceeds the 2,000,000-character configuration limit.");
        }
        if (nested) throw Error(location, "Unclosed interpolation expression. Add the closing }.");
        return output.ToString();
    }

    private static bool IsStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static ComposeConfigurationException Error(string location, string message) => new($"{location}: {message}");
}
