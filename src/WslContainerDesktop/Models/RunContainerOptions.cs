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

    /// <summary>Overrides the image entrypoint (compose <c>entrypoint:</c>). Free text, split like <see cref="Command"/>.</summary>
    public string? Entrypoint { get; set; }

    /// <summary>User to run the process as (compose <c>user:</c>, maps to <c>--user</c>).</summary>
    public string? User { get; set; }

    /// <summary>Working directory inside the container (compose <c>working_dir:</c>, maps to <c>--workdir</c>).</summary>
    public string? WorkingDir { get; set; }

    /// <summary>Container hostname (compose <c>hostname:</c>, maps to <c>--hostname</c>).</summary>
    public string? Hostname { get; set; }

    /// <summary>CPU limit (compose <c>cpus</c> / <c>deploy.resources.limits.cpus</c>, maps to <c>--cpus</c>).</summary>
    public string? CpuLimit { get; set; }

    /// <summary>Memory limit e.g. "512M" (compose <c>mem_limit</c> / <c>deploy.resources.limits.memory</c>, maps to <c>--memory</c>).</summary>
    public string? MemoryLimit { get; set; }

    /// <summary>
    /// Primary network to attach the container to (null/empty = engine default bridge). For a
    /// multi-network service this is the first network; the full set is kept in <see cref="Networks"/>.
    /// </summary>
    public string? Network { get; set; }

    /// <summary>
    /// All networks the service declared (compose <c>networks:</c>). <c>wslc run</c> attaches a
    /// container to a single network, so <see cref="ToArguments"/> uses the first entry; the rest are
    /// retained in the model/import for fidelity and future multi-attach support.
    /// </summary>
    public List<string> Networks { get; set; } = new();

    /// <summary>Raw "host:container" or "host:container/proto" strings.</summary>
    public List<string> PortMappings { get; set; } = new();

    /// <summary>Raw "KEY=VALUE" strings.</summary>
    public List<string> EnvironmentVariables { get; set; } = new();

    /// <summary>Raw "source:destination" volume/bind strings.</summary>
    public List<string> Volumes { get; set; } = new();

    /// <summary>Container metadata labels (maps to repeated <c>--label KEY=VALUE</c>). Used to tag compose-project members.</summary>
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Network-scoped aliases the container is reachable by on its network (maps to repeated
    /// <c>--network-alias</c>). The supervisor adds the compose service name so sibling services can
    /// resolve it by name, mirroring Compose's built-in DNS discovery.
    /// </summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>DNS nameserver IPs (compose <c>dns:</c>, maps to repeated <c>--dns</c>).</summary>
    public List<string> Dns { get; set; } = new();

    /// <summary>DNS search domains (compose <c>dns_search:</c>, maps to repeated <c>--dns-search</c>).</summary>
    public List<string> DnsSearch { get; set; } = new();

    /// <summary>DNS resolver options (compose <c>dns_opt:</c>, maps to repeated <c>--dns-option</c>).</summary>
    public List<string> DnsOptions { get; set; } = new();

    /// <summary>tmpfs mount targets (compose <c>tmpfs:</c>, maps to repeated <c>--tmpfs</c>).</summary>
    public List<string> Tmpfs { get; set; } = new();

    /// <summary>ulimit settings in <c>name=soft[:hard]</c> form (compose <c>ulimits:</c>, maps to repeated <c>--ulimit</c>).</summary>
    public List<string> Ulimits { get; set; } = new();

    /// <summary>Size of <c>/dev/shm</c> e.g. "64M" (compose <c>shm_size:</c>, maps to <c>--shm-size</c>).</summary>
    public string? ShmSize { get; set; }

    /// <summary>Signal used to stop the container (compose <c>stop_signal:</c>, maps to <c>--stop-signal</c>).</summary>
    public string? StopSignal { get; set; }

    /// <summary>Container domain name (compose <c>domainname:</c>, maps to <c>--domainname</c>).</summary>
    public string? Domainname { get; set; }

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

        // wslc run attaches a container to one network; use the primary (first declared) network.
        var primaryNetwork = !string.IsNullOrWhiteSpace(Network)
            ? Network!.Trim()
            : Networks.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim();
        if (!string.IsNullOrWhiteSpace(primaryNetwork))
        {
            args.Add("--network");
            args.Add(primaryNetwork);

            // Aliases only apply when attached to a user network.
            foreach (var alias in Aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal))
            {
                args.Add("--network-alias");
                args.Add(alias.Trim());
            }
        }

        foreach (var d in Dns.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            args.Add("--dns");
            args.Add(d.Trim());
        }

        foreach (var d in DnsSearch.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            args.Add("--dns-search");
            args.Add(d.Trim());
        }

        foreach (var d in DnsOptions.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            args.Add("--dns-option");
            args.Add(d.Trim());
        }

        foreach (var t in Tmpfs.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            args.Add("--tmpfs");
            args.Add(t.Trim());
        }

        foreach (var u in Ulimits.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            args.Add("--ulimit");
            args.Add(u.Trim());
        }

        if (!string.IsNullOrWhiteSpace(ShmSize))
        {
            args.Add("--shm-size");
            args.Add(ShmSize.Trim());
        }

        if (!string.IsNullOrWhiteSpace(StopSignal))
        {
            args.Add("--stop-signal");
            args.Add(StopSignal.Trim());
        }

        if (!string.IsNullOrWhiteSpace(Domainname))
        {
            args.Add("--domainname");
            args.Add(Domainname.Trim());
        }

        if (!string.IsNullOrWhiteSpace(Hostname))
        {
            args.Add("--hostname");
            args.Add(Hostname.Trim());
        }

        if (!string.IsNullOrWhiteSpace(User))
        {
            args.Add("--user");
            args.Add(User.Trim());
        }

        if (!string.IsNullOrWhiteSpace(WorkingDir))
        {
            args.Add("--workdir");
            args.Add(WorkingDir.Trim());
        }

        if (!string.IsNullOrWhiteSpace(CpuLimit))
        {
            args.Add("--cpus");
            args.Add(CpuLimit.Trim());
        }

        if (!string.IsNullOrWhiteSpace(MemoryLimit))
        {
            args.Add("--memory");
            args.Add(MemoryLimit.Trim());
        }

        if (!string.IsNullOrWhiteSpace(Entrypoint))
        {
            // wslc --entrypoint takes a single executable; pass the first token and fold any
            // remaining tokens into the command arguments below.
            var entryTokens = SplitCommand(Entrypoint).ToList();
            if (entryTokens.Count > 0)
            {
                args.Add("--entrypoint");
                args.Add(entryTokens[0]);
            }
        }

        foreach (var label in Labels.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)))
        {
            args.Add("--label");
            args.Add(string.IsNullOrEmpty(label.Value) ? label.Key : $"{label.Key}={label.Value}");
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

        // Any entrypoint tokens beyond the executable become leading command arguments, followed
        // by the explicit command. wslc's --entrypoint only accepts the executable itself.
        var entrypointTail = string.IsNullOrWhiteSpace(Entrypoint)
            ? Enumerable.Empty<string>()
            : SplitCommand(Entrypoint).Skip(1);
        args.AddRange(entrypointTail);

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
