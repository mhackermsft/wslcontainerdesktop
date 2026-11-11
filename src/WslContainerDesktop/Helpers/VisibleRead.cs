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

using WslContainerDesktop.Services;

namespace WslContainerDesktop.Helpers;

/// <summary>Cancels UI-only work when its lifetime ends or the window stops being viewable.</summary>
internal sealed class VisibleRead : IDisposable
{
    private readonly IWindowVisibility _visibility;
    private readonly CancellationTokenSource _cancellation;
    private readonly object _gate = new();
    private bool _disposed;

    internal VisibleRead(IWindowVisibility visibility, CancellationToken lifetime,
        CancellationToken additionalLifetime = default)
    {
        _visibility = visibility;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime, additionalLifetime);
        _visibility.Changed += OnVisibilityChanged;
        OnVisibilityChanged(null, EventArgs.Empty);
    }

    internal CancellationToken Token => _cancellation.Token;
    internal bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    private void OnVisibilityChanged(object? sender, EventArgs args)
    {
        lock (_gate)
            if (!_disposed && !_visibility.IsViewable) _cancellation.Cancel();
    }

    public void Dispose()
    {
        _visibility.Changed -= OnVisibilityChanged;
        lock (_gate)
        {
            _disposed = true;
            _cancellation.Dispose();
        }
    }
}
