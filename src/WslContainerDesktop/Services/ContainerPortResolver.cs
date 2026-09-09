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

using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Serializes enrichment across callers, with a four-inspect budget and expiring results.</summary>
internal sealed class ContainerPortResolver
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record Entry(long CreatedAt, int State, string Name, DateTimeOffset Expires, List<PortMapping>? Ports);

    internal async Task ResolveAsync(
        IReadOnlyList<ContainerInfo> containers,
        bool completeInventory,
        Func<string, CancellationToken, Task<CommandResult>> inspect,
        Action<string, string> reportFailure,
        CancellationToken ct,
        DateTimeOffset? timestamp = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = timestamp ?? DateTimeOffset.UtcNow;
            if (completeInventory)
            {
                var ids = containers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var id in _cache.Keys.Where(id => !ids.Contains(id)).ToArray())
                    _cache.Remove(id);
            }
            var budget = 4;
            // Never-before-inspected rows precede expired entries so large inventories make progress.
            foreach (var container in containers.Where(c => !c.PortsKnown)
                         .OrderBy(c => _cache.TryGetValue(c.Id, out var entry) ? entry.Expires : DateTimeOffset.MinValue))
            {
                if (_cache.TryGetValue(container.Id, out var cached) &&
                    cached.CreatedAt == container.CreatedAt && cached.State == container.StateValue &&
                    cached.Name == container.Name && cached.Expires > now)
                {
                    Apply(container, cached.Ports);
                    continue;
                }
                if (budget-- <= 0)
                    continue;

                List<PortMapping>? ports = null;
                var response = await inspect(container.Id, ct).ConfigureAwait(false);
                if (!response.Success)
                    reportFailure(container.Id, $"Inspect failed (exit {response.ExitCode}).");
                else
                {
                    try
                    {
                        var records = WslcJsonParser.ParseList<JsonElement>(response.StandardOutput);
                        if (records.Count != 1 || !MatchesId(container.Id, ContainerInfoJsonConverter.ReadString(records[0], "Id")))
                            throw new JsonException("Inspect did not return exactly the requested container.");
                        if (ContainerPortParser.TryInspect(records[0], JsonOptions, out var resolved))
                            ports = resolved;
                        else
                            reportFailure(container.Id, "Inspect did not report published-port configuration.");
                    }
                    catch (JsonException ex)
                    {
                        reportFailure(container.Id, ex.Message);
                    }
                }
                _cache[container.Id] = new Entry(container.CreatedAt, container.StateValue, container.Name,
                    now.AddSeconds(ports is null ? 30 : 300), ports);
                Apply(container, ports);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Inspect resolves one engine identifier, never an arbitrary prefix match across inventory rows.
    internal static bool MatchesId(string requested, string inspected) =>
        string.Equals(requested, inspected, StringComparison.Ordinal) ||
        requested.Length >= 12 && inspected.StartsWith(requested, StringComparison.Ordinal);

    private static void Apply(ContainerInfo container, List<PortMapping>? ports)
    {
        if (ports is null)
            return; // The caller retains PortsKnown=false, not a successful empty configuration.
        container.Ports = ports.Select(p => new PortMapping
        {
            BindingAddress = p.BindingAddress, HostPort = p.HostPort,
            ContainerPort = p.ContainerPort, Protocol = p.Protocol,
        }).ToList();
        container.PortsKnown = true;
    }
}
