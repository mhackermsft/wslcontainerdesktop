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
    /// <summary>Gets or sets the image.</summary>
    public string Image { get; set; } = string.Empty;
    /// <summary>Gets or sets the name.</summary>
    public string? Name { get; set; }
    /// <summary>Gets or sets a value indicating whether this value is detached.</summary>
    public bool Detached { get; set; } = true;
    /// <summary>Gets or sets a value indicating whether the remove on exit flag is set.</summary>
    public bool RemoveOnExit { get; set; }
    /// <summary>Gets or sets a value indicating whether this value is interactive.</summary>
    public bool Interactive { get; set; }

    /// <summary>Why <see cref="WaitsForTerminalInput"/> options cannot be run from the app.</summary>
    public const string ForegroundInteractiveError =
        "Keep STDIN open (-i) needs Run in background (-d). The app has no terminal to attach to a " +
        "foreground container, so the run would wait forever. Turn on -d, then open a terminal to the " +
        "container to interact with it.";

    /// <summary>
    /// True when `wslc run` would hold stdin open in the foreground (`-i` without `-d`) and stay busy
    /// until the container exits, since the app's hidden console can never supply input. A method, not
    /// a property, so it is not persisted with run profiles.
    /// </summary>
    public bool WaitsForTerminalInput() => Interactive && !Detached;

    /// <summary>Gets or sets a value indicating whether the all gpus flag is set.</summary>
    public bool AllGpus { get; set; }
    /// <summary>Caller must verify --pull support before selecting cached-only creation.</summary>
    public bool NeverPull { get; set; }
    /// <summary>Gets or sets the command.</summary>
    public string? Command { get; set; }
    /// <summary>Gets or sets the health.</summary>
    public NativeHealthOptions? Health { get; set; }

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
    /// All networks the service declared. Retained for compatibility with saved projects/profiles.
    /// The Compose supervisor attaches additional endpoints when the engine supports it.
    /// </summary>
    public List<string> Networks { get; set; } = new();

    /// <summary>Additive per-network configuration. Empty in projects saved by earlier versions.</summary>
    public List<NetworkAttachment> NetworkAttachments { get; set; } = new();

    /// <summary>Explicit Compose network_mode, which must not receive ordinary network endpoints.</summary>
    public string? NetworkMode { get; set; }

    /// <summary>Gets a value indicating whether this value has special network mode.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSpecialNetworkMode => !string.IsNullOrWhiteSpace(NetworkMode) ||
        (NetworkAttachments.Count == 0 &&
         (Network is "host" or "none" or "bridge" ||
          Network?.StartsWith("container:", StringComparison.Ordinal) == true ||
          Network?.StartsWith("service:", StringComparison.Ordinal) == true));

    /// <summary>Normalizes old and new saved options without changing their desired configuration.</summary>
    public List<NetworkAttachment> GetNetworkAttachments()
    {
        if (HasSpecialNetworkMode)
        {
            return new();
        }

        var names = new[] { Network }.Concat(Networks).Concat(NetworkAttachments.Select(n => n.Network))
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.Ordinal);
        var result = new List<NetworkAttachment>();
        foreach (var name in names)
        {
            var declarations = NetworkAttachments.Where(n => n.Network.Trim() == name).ToList();
            var ips = declarations.Select(n => n.Ipv4Address).Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Distinct(StringComparer.Ordinal).ToList();
            if (ips.Count > 1)
            {
                throw new InvalidOperationException($"Network '{name}' has conflicting IPv4 addresses.");
            }

            result.Add(new NetworkAttachment
            {
                Network = name,
                Ipv4Address = ips.FirstOrDefault(),
                Aliases = declarations.SelectMany(n => n.Aliases)
                    .Concat(result.Count == 0 ? Aliases : Enumerable.Empty<string>())
                    .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())
                    .Distinct(StringComparer.Ordinal).ToList(),
            });
        }

        return result;
    }

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

    /// <summary>Seconds to wait before killing the container on stop (<c>--stop-timeout</c>); -1 means never kill.</summary>
    public int? StopTimeoutSeconds { get; set; }

    /// <summary>Container domain name (compose <c>domainname:</c>, maps to <c>--domainname</c>).</summary>
    public string? Domainname { get; set; }

    /// <summary>Structured native <c>--mount</c> entries. Short-form volume specs remain in <see cref="Volumes"/>.</summary>
    public List<RunContainerMount> Mounts { get; set; } = new();

    /// <summary>Deep-copies these options so callers (e.g. templates) can hand out an editable instance
    /// without mutating a shared source.</summary>
    public RunContainerOptions Clone() => new()
    {
        Image = Image,
        Name = Name,
        Detached = Detached,
        RemoveOnExit = RemoveOnExit,
        Interactive = Interactive,
        AllGpus = AllGpus,
        NeverPull = NeverPull,
        Command = Command,
        Health = Health?.Clone(),
        Entrypoint = Entrypoint,
        User = User,
        WorkingDir = WorkingDir,
        Hostname = Hostname,
        CpuLimit = CpuLimit,
        MemoryLimit = MemoryLimit,
        Network = Network,
        Networks = new List<string>(Networks),
        NetworkAttachments = NetworkAttachments.Select(n => n.Clone()).ToList(),
        NetworkMode = NetworkMode,
        PortMappings = new List<string>(PortMappings),
        EnvironmentVariables = new List<string>(EnvironmentVariables),
        Volumes = new List<string>(Volumes),
        Labels = new Dictionary<string, string>(Labels, StringComparer.Ordinal),
        Aliases = new List<string>(Aliases),
        Dns = new List<string>(Dns),
        DnsSearch = new List<string>(DnsSearch),
        DnsOptions = new List<string>(DnsOptions),
        Tmpfs = new List<string>(Tmpfs),
        Ulimits = new List<string>(Ulimits),
        ShmSize = ShmSize,
        StopSignal = StopSignal,
        StopTimeoutSeconds = StopTimeoutSeconds,
        Domainname = Domainname,
        Mounts = Mounts.Select(m => m.Clone()).ToList(),
    };

    /// <summary>
    /// Returns the container path of a <c>-v</c> spec: <c>source:/target[:mode]</c>, or just
    /// <c>/target</c> for an anonymous volume. The last <c>:/</c> is used so a Windows source such as
    /// <c>C:/data</c> is not mistaken for the boundary.
    /// </summary>
    internal static string VolumeSpecTarget(string spec)
    {
        var value = spec.Trim();
        var boundary = value.LastIndexOf(":/", StringComparison.Ordinal);
        var target = boundary >= 0 ? value[(boundary + 1)..] : value;
        return target.Split(':', 2)[0];
    }

    /// <summary>Converts model data for to arguments scenarios.</summary>
    /// <param name="healthArguments">The health arguments value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public List<string> ToArguments(IReadOnlyList<string>? healthArguments = null) =>
        BuildArguments(create: false, healthArguments);

    /// <summary>Converts model data for to create arguments scenarios.</summary>
    /// <param name="healthArguments">The health arguments value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public List<string> ToCreateArguments(IReadOnlyList<string>? healthArguments = null) =>
        BuildArguments(create: true, healthArguments);

    private List<string> BuildArguments(bool create, IReadOnlyList<string>? healthArguments)
    {
        var args = new List<string> { create ? "create" : "run" };
        if (NeverPull)
        {
            args.Add("--pull");
            args.Add("never");
        }

        if (Detached && !create)
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
        var endpoint = GetNetworkAttachments().FirstOrDefault();
        var primaryNetwork = !string.IsNullOrWhiteSpace(NetworkMode) ? NetworkMode :
            !string.IsNullOrWhiteSpace(Network)
            ? Network!.Trim()
            : endpoint?.Network;
        if (!string.IsNullOrWhiteSpace(primaryNetwork))
        {
            args.Add("--network");
            args.Add(primaryNetwork);

            endpoint?.AddEndpointArguments(args);
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

        if (StopTimeoutSeconds is int stopTimeout)
        {
            if (stopTimeout < -1)
            {
                throw new ArgumentException("--stop-timeout must be -1 or a nonnegative number of seconds.");
            }

            args.Add("--stop-timeout");
            args.Add(stopTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

        // Compose keeps every volume in both lists: a -v string in Volumes and a structured copy in
        // Mounts. An exact match is emitted once, as --mount. Later steps (project-name prefixing,
        // redeploy renames, keeping an anonymous volume on recreate) rewrite only the Volumes copy,
        // so a volume-type mount whose target a -v entry now covers is stale and is skipped;
        // passing both makes WSLC reject the run with "Duplicate mount point".
        var nativeMountSpecs = Mounts.Select(m => m.ToVolumeSpec()).ToHashSet(StringComparer.Ordinal);
        var volumeTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in Volumes.Where(x => !string.IsNullOrWhiteSpace(x) && !nativeMountSpecs.Contains(x.Trim())))
        {
            args.Add("-v");
            args.Add(v.Trim());
            volumeTargets.Add(VolumeSpecTarget(v));
        }

        foreach (var mount in Mounts)
        {
            if (mount.Type.Equals("volume", StringComparison.OrdinalIgnoreCase) &&
                volumeTargets.Contains(mount.Target.Trim()))
            {
                continue;
            }

            args.Add("--mount");
            args.Add(mount.ToArgument());
        }

        if (healthArguments is not null)
        {
            args.AddRange(healthArguments);
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
        var tokenStarted = false;
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
                tokenStarted = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (tokenStarted)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    tokenStarted = false;
                }
            }
            else
            {
                sb.Append(c);
                tokenStarted = true;
            }
        }

        if (tokenStarted)
        {
            result.Add(sb.ToString());
        }

        return result;
    }
}
