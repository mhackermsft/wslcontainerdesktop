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

namespace WslContainerDesktop.Models;

/// <summary>User-supplied options for `wslc run`, assembled by the Run dialog.</summary>
public sealed class RunContainerOptions
{
    public string Image { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool Detached { get; set; } = true;
    public bool RemoveOnExit { get; set; }
    public bool Interactive { get; set; }
    public bool AllGpus { get; set; }
    public string? Command { get; set; }

    /// <summary>Network to attach the container to (null/empty = engine default bridge).</summary>
    public string? Network { get; set; }

    /// <summary>Raw "host:container" or "host:container/proto" strings.</summary>
    public List<string> PortMappings { get; set; } = new();

    /// <summary>Raw "KEY=VALUE" strings.</summary>
    public List<string> EnvironmentVariables { get; set; } = new();

    /// <summary>Raw "source:destination" volume/bind strings.</summary>
    public List<string> Volumes { get; set; } = new();

    public List<string> ToArguments()
    {
        var args = new List<string> { "run" };

        if (Detached)
        {
            args.Add("-d");
        }

        if (RemoveOnExit)
        {
            args.Add("--rm");
        }

        if (Interactive)
        {
            args.Add("-i");
        }

        if (AllGpus)
        {
            args.Add("--gpus");
            args.Add("all");
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            args.Add("--name");
            args.Add(Name.Trim());
        }

        if (!string.IsNullOrWhiteSpace(Network))
        {
            args.Add("--network");
            args.Add(Network.Trim());
        }

        foreach (var p in PortMappings.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            args.Add("-p");
            args.Add(p.Trim());
        }

        foreach (var e in EnvironmentVariables.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            args.Add("-e");
            args.Add(e.Trim());
        }

        foreach (var v in Volumes.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            args.Add("-v");
            args.Add(v.Trim());
        }

        args.Add(Image.Trim());

        if (!string.IsNullOrWhiteSpace(Command))
        {
            // Command entered as free text; split on whitespace respecting simple quotes.
            args.AddRange(SplitCommand(Command));
        }

        return args;
    }

    private static IEnumerable<string> SplitCommand(string command)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        char quoteChar = '"';

        foreach (var c in command)
        {
            if (inQuotes)
            {
                if (c == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                inQuotes = true;
                quoteChar = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
        {
            result.Add(sb.ToString());
        }

        return result;
    }
}
