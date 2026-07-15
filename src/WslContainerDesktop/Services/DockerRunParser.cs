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
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Parses a pasted <c>docker run</c> / <c>podman run</c> command line into
/// <see cref="RunContainerOptions"/> so the Run dialog can be prefilled from a snippet found in a
/// README or blog post. Recognized flags are mapped onto the options; anything that cannot be
/// represented is reported through <see cref="DockerRunParseResult.Warnings"/> rather than failing.
/// Never throws for malformed input.
/// </summary>
public static class DockerRunParser
{
    // Long options that consume a following value (either "--flag value" or "--flag=value").
    private static readonly HashSet<string> ValueLongFlags = new(StringComparer.Ordinal)
    {
        "--name", "--network", "--net", "--network-alias", "--publish", "--env", "--env-file",
        "--volume", "--mount", "--workdir", "--user", "--hostname", "--entrypoint", "--gpus",
        "--label", "--cpus", "--memory", "--memory-swap", "--shm-size", "--stop-signal", "--dns",
        "--dns-search", "--dns-option", "--tmpfs", "--ulimit", "--domainname", "--restart",
        "--platform", "--pull", "--add-host", "--cap-add", "--cap-drop", "--device", "--expose",
        "--health-cmd", "--label-file", "--log-driver", "--pid", "--ipc", "--userns",
    };

    // Short options that consume a value; the value may be attached ("-p8080:80") or the next token.
    private static readonly HashSet<char> ValueShortFlags = new() { 'p', 'e', 'v', 'u', 'w', 'l', 'h', 'm' };

    /// <summary>Parses <paramref name="commandLine"/>. Returns a result whose Options is null when no image is found.</summary>
    public static DockerRunParseResult Parse(string? commandLine)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return new DockerRunParseResult(null, warnings);
        }

        var tokens = Tokenize(commandLine);
        var index = SkipPrefix(tokens);
        var options = new RunContainerOptions();
        var sawDetach = false;

        string? image = null;
        var command = new List<string>();

        while (index < tokens.Count)
        {
            var token = tokens[index];

            // Once the image is captured, every remaining token is the container command/args.
            if (image is not null)
            {
                command.Add(token);
                index++;
                continue;
            }

            if (token == "--")
            {
                index++;
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                index = HandleLongFlag(tokens, index, token, options, warnings, ref sawDetach);
                continue;
            }

            if (token.StartsWith('-') && token.Length > 1)
            {
                index = HandleShortCluster(tokens, index, token, options, warnings, ref sawDetach);
                continue;
            }

            // First bare token is the image reference.
            image = token;
            index++;
        }

        if (string.IsNullOrWhiteSpace(image))
        {
            return new DockerRunParseResult(null, warnings);
        }

        options.Image = image.Trim();
        options.Detached = sawDetach;
        if (command.Count > 0)
        {
            options.Command = string.Join(' ', command.Select(QuoteIfNeeded));
        }

        return new DockerRunParseResult(options, warnings);
    }

    private static int HandleLongFlag(
        List<string> tokens, int index, string token, RunContainerOptions options,
        List<string> warnings, ref bool sawDetach)
    {
        var eq = token.IndexOf('=');
        var name = eq >= 0 ? token[..eq] : token;
        string? inlineValue = eq >= 0 ? token[(eq + 1)..] : null;

        // Normalize a couple of common aliases.
        if (name == "--net")
        {
            name = "--network";
        }

        // Boolean long flags.
        switch (name)
        {
            case "--detach":
                sawDetach = true;
                return index + 1;
            case "--rm":
                options.RemoveOnExit = true;
                return index + 1;
            case "--interactive":
                options.Interactive = true;
                return index + 1;
            case "--tty":
            case "--privileged":
            case "--init":
                // Benign or unrepresentable booleans; ignored silently (very common with runs).
                return index + 1;
        }

        // Value long flags.
        if (ValueLongFlags.Contains(name) || inlineValue is not null)
        {
            var value = inlineValue;
            var consumed = 1;
            if (value is null)
            {
                if (index + 1 >= tokens.Count)
                {
                    warnings.Add($"Ignored '{name}': no value provided.");
                    return index + 1;
                }

                value = tokens[index + 1];
                consumed = 2;
            }

            ApplyValueFlag(name, value, options, warnings);
            return index + consumed;
        }

        // Unknown flag. Docker requires the image reference as the final positional argument, so if
        // the next token isn't another flag and isn't the last token, assume this flag takes a value
        // and skip both — otherwise the value would be misread as the image. If the next token IS the
        // last one, treat the flag as boolean so the image is preserved.
        if (index + 1 < tokens.Count - 1 &&
            !tokens[index + 1].StartsWith('-'))
        {
            warnings.Add($"Ignored unsupported flag '{name} {tokens[index + 1]}'.");
            return index + 2;
        }

        warnings.Add($"Ignored unrecognized flag '{name}'.");
        return index + 1;
    }

    private static int HandleShortCluster(
        List<string> tokens, int index, string token, RunContainerOptions options,
        List<string> warnings, ref bool sawDetach)
    {
        // token like "-it", "-d", "-p8080:80", "-e", "-p".
        var chars = token[1..];
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (ValueShortFlags.Contains(c))
            {
                // Value is the rest of this cluster, or the next token.
                var attached = chars[(i + 1)..];
                string? value;
                if (attached.Length > 0)
                {
                    value = attached;
                    ApplyShortValueFlag(c, value, options, warnings);
                    return index + 1;
                }

                if (index + 1 >= tokens.Count)
                {
                    warnings.Add($"Ignored '-{c}': no value provided.");
                    return index + 1;
                }

                value = tokens[index + 1];
                ApplyShortValueFlag(c, value, options, warnings);
                return index + 2;
            }

            switch (c)
            {
                case 'd':
                    sawDetach = true;
                    break;
                case 'i':
                    options.Interactive = true;
                    break;
                case 't':
                    // No TTY option to represent; ignored (ubiquitous alongside -i).
                    break;
                default:
                    warnings.Add($"Ignored unrecognized flag '-{c}'.");
                    break;
            }
        }

        return index + 1;
    }

    private static void ApplyShortValueFlag(char c, string value, RunContainerOptions options, List<string> warnings)
    {
        var name = c switch
        {
            'p' => "--publish",
            'e' => "--env",
            'v' => "--volume",
            'u' => "--user",
            'w' => "--workdir",
            'l' => "--label",
            'h' => "--hostname",
            'm' => "--memory",
            _ => null,
        };

        if (name is not null)
        {
            ApplyValueFlag(name, value, options, warnings);
        }
    }

    private static void ApplyValueFlag(string name, string value, RunContainerOptions options, List<string> warnings)
    {
        value = value.Trim();
        switch (name)
        {
            case "--name":
                options.Name = value;
                break;
            case "--network":
                options.Network = value;
                options.Networks.Add(value);
                break;
            case "--network-alias":
                options.Aliases.Add(value);
                break;
            case "--publish":
                options.PortMappings.Add(value);
                break;
            case "--env":
                options.EnvironmentVariables.Add(value);
                break;
            case "--volume":
                options.Volumes.Add(value);
                break;
            case "--workdir":
                options.WorkingDir = value;
                break;
            case "--user":
                options.User = value;
                break;
            case "--hostname":
                options.Hostname = value;
                break;
            case "--entrypoint":
                options.Entrypoint = value;
                break;
            case "--gpus":
                options.AllGpus = true;
                if (!value.Equals("all", StringComparison.OrdinalIgnoreCase) && value != "-1")
                {
                    warnings.Add($"'--gpus {value}' imported as all GPUs (per-device selection isn't supported).");
                }

                break;
            case "--label":
                AddLabel(options, value);
                break;
            case "--cpus":
                options.CpuLimit = value;
                break;
            case "--memory":
                options.MemoryLimit = value;
                break;
            case "--shm-size":
                options.ShmSize = value;
                break;
            case "--stop-signal":
                options.StopSignal = value;
                break;
            case "--dns":
                options.Dns.Add(value);
                break;
            case "--dns-search":
                options.DnsSearch.Add(value);
                break;
            case "--dns-option":
                options.DnsOptions.Add(value);
                break;
            case "--tmpfs":
                options.Tmpfs.Add(value);
                break;
            case "--ulimit":
                options.Ulimits.Add(value);
                break;
            case "--domainname":
                options.Domainname = value;
                break;
            case "--mount":
                warnings.Add("'--mount' isn't supported; use '-v source:destination' instead. Skipped.");
                break;
            case "--env-file":
                warnings.Add($"'--env-file {value}' isn't read on import; add the variables manually. Skipped.");
                break;
            case "--restart":
                warnings.Add($"'--restart {value}' isn't part of a run profile (managed via Compose). Skipped.");
                break;
            case "--memory-swap":
            case "--platform":
            case "--pull":
            case "--add-host":
            case "--cap-add":
            case "--cap-drop":
            case "--device":
            case "--expose":
            case "--health-cmd":
            case "--label-file":
            case "--log-driver":
            case "--pid":
            case "--ipc":
            case "--userns":
                warnings.Add($"'{name} {value}' isn't supported and was skipped.");
                break;
            default:
                warnings.Add($"Ignored unrecognized flag '{name}'.");
                break;
        }
    }

    private static void AddLabel(RunContainerOptions options, string value)
    {
        var eq = value.IndexOf('=');
        if (eq < 0)
        {
            options.Labels[value] = string.Empty;
        }
        else
        {
            options.Labels[value[..eq]] = value[(eq + 1)..];
        }
    }

    /// <summary>Drops a leading <c>sudo</c>/<c>docker</c>/<c>podman</c> and the <c>run</c> subcommand.</summary>
    private static int SkipPrefix(List<string> tokens)
    {
        var i = 0;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t is "sudo" or "docker" or "podman" or "docker.exe" or "podman.exe" or "nerdctl")
            {
                i++;
                continue;
            }

            if (t == "run")
            {
                i++;
            }

            break;
        }

        return i;
    }

    /// <summary>
    /// Splits a command line into tokens, honoring single/double quotes and backslash line
    /// continuations (a trailing <c>\</c> before a newline is dropped). Quote characters are removed.
    /// </summary>
    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inToken = false;
        var quote = '\0';

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    sb.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    quote = c;
                    inToken = true;
                    break;
                case '\\':
                    // Line continuation: swallow the backslash and the following newline(s).
                    if (i + 1 < input.Length && (input[i + 1] == '\n' || input[i + 1] == '\r'))
                    {
                        i++;
                        while (i + 1 < input.Length && (input[i + 1] == '\n' || input[i + 1] == '\r'))
                        {
                            i++;
                        }
                    }
                    else if (i + 1 < input.Length)
                    {
                        // Escaped character: keep the next char literally.
                        sb.Append(input[i + 1]);
                        i++;
                        inToken = true;
                    }

                    break;
                default:
                    if (char.IsWhiteSpace(c))
                    {
                        if (inToken)
                        {
                            tokens.Add(sb.ToString());
                            sb.Clear();
                            inToken = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                        inToken = true;
                    }

                    break;
            }
        }

        if (inToken)
        {
            tokens.Add(sb.ToString());
        }

        return tokens;
    }

    private static string QuoteIfNeeded(string token) =>
        token.Any(char.IsWhiteSpace) ? $"\"{token}\"" : token;
}

/// <summary>Outcome of parsing a <c>docker run</c> command line.</summary>
/// <param name="Options">The parsed options, or null when no image reference was found.</param>
/// <param name="Warnings">Human-readable notes about flags that could not be represented.</param>
public sealed record DockerRunParseResult(RunContainerOptions? Options, IReadOnlyList<string> Warnings);
