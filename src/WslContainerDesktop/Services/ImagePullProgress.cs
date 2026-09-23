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

using System.Text.RegularExpressions;

namespace WslContainerDesktop.Services;

/// <summary>
/// Turns the per-layer status lines <c>wslc pull</c> prints when its output is redirected
/// (<c>25f1d6b1951a: Pulling fs layer</c>, <c>…: Download complete</c>, <c>…: Pull complete</c>)
/// into a short "N of M layers" message. The redirected output carries no byte counts, so layer
/// counts are the only honest measure. Safe to call from the stdout and stderr reader threads.
/// </summary>
public sealed partial class ImagePullProgress(string subject)
{
    private enum LayerState
    {
        Seen,
        Downloaded,
        Ready,
    }

    private readonly Dictionary<string, LayerState> _layers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private string? _lastMessage;

    [GeneratedRegex(@"^\s*(?<id>[0-9a-f]{12,64}):\s+(?<status>\S.*?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex LayerLine();

    /// <summary>Records one output line; returns a message only when the visible progress changed.</summary>
    public string? Observe(string line)
    {
        var match = LayerLine().Match(line);
        if (!match.Success)
            return null;

        var status = match.Groups["status"].Value;
        var state = status.StartsWith("Pull complete", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Already exists", StringComparison.OrdinalIgnoreCase)
                ? LayerState.Ready
            : status.StartsWith("Download complete", StringComparison.OrdinalIgnoreCase)
                || status.StartsWith("Extracting", StringComparison.OrdinalIgnoreCase)
                ? LayerState.Downloaded
            : LayerState.Seen;

        lock (_gate)
        {
            var id = match.Groups["id"].Value;
            if (!_layers.TryGetValue(id, out var current) || state > current)
                _layers[id] = state;

            var total = _layers.Count;
            var downloaded = _layers.Values.Count(s => s >= LayerState.Downloaded);
            var ready = _layers.Values.Count(s => s == LayerState.Ready);
            var message = downloaded < total
                ? $"Downloading {subject}: {downloaded} of {total} layers downloaded..."
                : $"Unpacking {subject}: {ready} of {total} layers ready...";

            if (message == _lastMessage)
                return null;

            _lastMessage = message;
            return message;
        }
    }
}
