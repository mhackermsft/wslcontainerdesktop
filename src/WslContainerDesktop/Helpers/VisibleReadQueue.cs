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

namespace WslContainerDesktop.Helpers;

/// <summary>Bounds active UI reads without creating tasks for queued, off-screen or completed keys.</summary>
internal sealed class VisibleReadQueue<T>(
    int concurrency, Func<T, CancellationToken, Task> read, Action<Exception> reportError) : IDisposable where T : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<T, Entry> _visible = [];
    private readonly HashSet<T> _active = [];
    private bool _enabled;
    private bool _disposed;

    internal void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled && !_disposed;
            Pump();
        }
    }

    internal void SetVisible(T key, bool visible)
    {
        Entry? removed = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (visible)
            {
                if (!_visible.ContainsKey(key)) _visible.Add(key, new Entry(key));
            }
            else _visible.Remove(key, out removed);
            Pump();
        }
        removed?.Dispose();
    }

    internal void Refresh()
    {
        lock (_gate)
        {
            foreach (var entry in _visible.Values)
            {
                if (_active.Contains(entry.Key)) entry.Retry = true;
                else entry.Started = false;
            }
            Pump();
        }
    }

    internal void Clear()
    {
        Entry[] removed;
        lock (_gate)
        {
            removed = _visible.Values.ToArray();
            _visible.Clear();
        }
        foreach (var entry in removed) entry.Dispose();
    }

    private void Pump()
    {
        if (!_enabled || _disposed) return;
        foreach (var entry in _visible.Values)
        {
            if (_active.Count >= concurrency) break;
            if (entry.Started || !_active.Add(entry.Key)) continue;
            entry.Started = true;
            _ = RunAsync(entry);
        }
    }

    private async Task RunAsync(Entry entry)
    {
        // Keep cache hits and synchronous callbacks from recursively pumping on the UI thread.
        await Task.Yield();
        try
        {
            entry.Token.ThrowIfCancellationRequested();
            await read(entry.Key, entry.Token);
        }
        catch (OperationCanceledException) when (entry.Token.IsCancellationRequested) { }
        catch (Exception ex) { reportError(ex); }
        finally
        {
            lock (_gate)
            {
                _active.Remove(entry.Key);
                if (entry.Retry)
                {
                    entry.Retry = false;
                    entry.Started = false;
                }
                Pump();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _enabled = false;
        }
        Clear();
    }

    private sealed class Entry : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        internal Entry(T key)
        {
            Key = key;
            Token = _stop.Token;
        }
        internal T Key { get; }
        internal CancellationToken Token { get; }
        internal bool Started { get; set; }
        internal bool Retry { get; set; }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
