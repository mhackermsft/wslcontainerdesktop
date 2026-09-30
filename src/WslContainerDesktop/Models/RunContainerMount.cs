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

namespace WslContainerDesktop.Models;

/// <summary>Structured <c>--mount</c> descriptor for <c>wslc run/create</c>.</summary>
public sealed class RunContainerMount
{
    private static readonly HashSet<string> SupportedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "bind", "volume", "tmpfs" };

    /// <summary>Gets or sets the type.</summary>
    public string Type { get; set; } = "bind";
    /// <summary>Gets or sets the source.</summary>
    public string? Source { get; set; }
    /// <summary>Gets or sets the target.</summary>
    public string Target { get; set; } = string.Empty;
    /// <summary>Gets or sets a value indicating whether the read only flag is set.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Additional validated <c>key=value</c> tokens passed through to <c>--mount</c>. WSLC 3.0.1
    /// accepts only a small Docker-compatible subset; callers must add only options verified safe.
    /// </summary>
    public List<string> Options { get; set; } = new();

    /// <summary>Creates a copy so callers can edit options without mutating the original instance.</summary>
    /// <returns>The requested value for the caller.</returns>
    public RunContainerMount Clone() => new()
    {
        Type = Type,
        Source = Source,
        Target = Target,
        ReadOnly = ReadOnly,
        Options = new List<string>(Options),
    };

    /// <summary>Converts model data for to argument scenarios.</summary>
    /// <returns>The requested value for the caller.</returns>
    public string ToArgument()
    {
        ValidateToken("type", Type, requireValue: true);
        if (!SupportedTypes.Contains(Type))
        {
            throw new ArgumentException($"Unsupported mount type '{Type}'.");
        }

        ValidateToken("target", Target, requireValue: true);
        var items = new List<string> { "type=" + Type.Trim().ToLowerInvariant() };
        if (!string.IsNullOrWhiteSpace(Source))
        {
            ValidateToken("source", Source!, requireValue: true);
            items.Add("source=" + Source!.Trim());
        }
        else if (Type.Equals("bind", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Mount type '{Type}' requires a source.");
        }

        items.Add("target=" + Target.Trim());
        if (ReadOnly)
        {
            items.Add("readonly");
        }

        foreach (var option in Options.Where(o => !string.IsNullOrWhiteSpace(o)))
        {
            var trimmed = option.Trim();
            var equals = trimmed.IndexOf('=');
            if (equals <= 0 || equals == trimmed.Length - 1)
            {
                throw new ArgumentException($"Mount option '{option}' must be key=value.");
            }

            ValidateKey(trimmed[..equals]);
            ValidateToken(trimmed[..equals], trimmed[(equals + 1)..], requireValue: true);
            items.Add(trimmed);
        }

        return string.Join(',', items);
    }

    /// <summary>Converts model data for to volume spec scenarios.</summary>
    /// <returns>The requested value for the caller.</returns>
    public string ToVolumeSpec()
    {
        ValidateToken("target", Target, requireValue: true);
        var spec = string.IsNullOrWhiteSpace(Source) ? Target.Trim() : $"{Source!.Trim()}:{Target.Trim()}";
        return ReadOnly ? spec + ":ro" : spec;
    }

    /// <summary>Parses input into try parse data used by the app.</summary>
    /// <param name="spec">The spec value supplied by the caller.</param>
    /// <param name="mount">The mount value supplied by the caller.</param>
    /// <param name="error">The error value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public static bool TryParse(string spec, out RunContainerMount? mount, out string? error)
    {
        mount = null;
        error = null;
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var options = new List<string>();
            var readOnly = false;
            foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw.Equals("readonly", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("ro", StringComparison.OrdinalIgnoreCase))
                {
                    readOnly = true;
                    continue;
                }

                var equals = raw.IndexOf('=');
                if (equals <= 0 || equals == raw.Length - 1)
                {
                    throw new ArgumentException($"Mount token '{raw}' must be key=value or readonly.");
                }

                var key = raw[..equals].Trim();
                var value = raw[(equals + 1)..].Trim();
                switch (key.ToLowerInvariant())
                {
                    case "type":
                    case "source":
                    case "src":
                    case "target":
                    case "dst":
                    case "destination":
                        values[key] = value;
                        break;
                    default:
                        ValidateKey(key);
                        ValidateToken(key, value, requireValue: true);
                        options.Add($"{key}={value}");
                        break;
                }
            }

            var type = values.GetValueOrDefault("type") ?? "volume";
            var source = values.GetValueOrDefault("source") ?? values.GetValueOrDefault("src");
            var target = values.GetValueOrDefault("target") ?? values.GetValueOrDefault("dst") ??
                values.GetValueOrDefault("destination");
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new ArgumentException("Mount target is required.");
            }

            mount = new RunContainerMount
            {
                Type = type,
                Source = source,
                Target = target,
                ReadOnly = readOnly,
                Options = options,
            };
            _ = mount.ToArgument();
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            mount = null;
            return false;
        }
    }

    private static void ValidateToken(string key, string value, bool requireValue)
    {
        ValidateKey(key);
        if (requireValue && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Mount option '{key}' requires a value.");
        }

        if (value != value.Trim() || value.Any(c => char.IsControl(c) || c == ','))
        {
            throw new ArgumentException($"Mount option '{key}' contains unsupported characters.");
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
        {
            throw new ArgumentException($"Mount option key '{key}' is invalid.");
        }
    }
}
