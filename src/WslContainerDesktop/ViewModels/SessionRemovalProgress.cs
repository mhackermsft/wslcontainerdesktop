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

using CommunityToolkit.Mvvm.ComponentModel;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>Session-removal feedback that survives session-list refreshes.</summary>
public sealed class SessionRemovalProgress : ObservableObject
{
    private string _name = string.Empty;
    private string? _error;
    private bool _finished;
    private bool _isBusy;
    private string? _previouslyUnavailable;

    public bool IsBusy => _isBusy;
    public bool CanEdit => !_isBusy;
    public bool IsSuccess => _finished && _error is null;
    public bool IsFailure => _finished && _error is not null;
    public string Message => _isBusy
        ? UiText.Get("ManagedSession_Removing", "Removing session {0}…", _name)
        : _error is not null
            ? UiText.Get("ManagedSession_RemoveFailed", "Could not remove session {0}: {1}", _name, _error)
            : _finished ? UiText.Get("ManagedSession_Removed", "Session {0} was removed.", _name) : string.Empty;

    public bool TryBegin(string name, string? previouslyUnavailable = null)
    {
        if (_isBusy)
            return false;
        _name = name;
        _previouslyUnavailable = previouslyUnavailable;
        _error = null;
        _finished = false;
        _isBusy = true;
        Refresh();
        return true;
    }

    public void Complete(string? error = null)
    {
        _error = error;
        _finished = true;
        _isBusy = false;
        Refresh();
    }

    public void RefreshLanguage() => Refresh();
    public void ClearCompleted()
    {
        if (_isBusy) return;
        _error = null;
        _finished = false;
        Refresh();
    }
    internal bool SuppressesNewUnavailableWarning(string name) =>
        IsBusy && string.Equals(_name, name, StringComparison.Ordinal) &&
        !string.Equals(_previouslyUnavailable, name, StringComparison.Ordinal);
    private void Refresh()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsSuccess));
        OnPropertyChanged(nameof(IsFailure));
        OnPropertyChanged(nameof(Message));
    }
}
