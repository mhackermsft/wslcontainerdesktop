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

/// <summary>Session stop/start feedback that survives session-list refreshes.</summary>
public sealed class SessionStopProgress : ObservableObject
{
    private string _name = string.Empty;
    private string? _error;
    private bool _starting;
    private bool _defaultSession;
    private bool _finished;
    private bool _busy;

    public bool IsBusy => _busy;
    internal bool IsStoppingDefault => _busy && _defaultSession && !_starting;
    public bool CanEdit => !_busy;
    public bool IsSuccess => _finished && _error is null;
    public bool IsFailure => _finished && _error is not null;
    private string NameDisplay => _defaultSession
        ? UiText.Get("WslEngine_Session_Default", "Default CLI session") : _name;
    public string Message => _busy
        ? _starting
            ? UiText.Get("ManagedSession_StartingProgress", "Starting or resuming session {0}…", NameDisplay)
            : UiText.Get("ManagedSession_StoppingProgress", "Stopping session {0}…", NameDisplay)
        : _error is not null
            ? _defaultSession
                ? UiText.Get("ManagedSession_DefaultActionFailed",
                    "The action for {0} failed: {1}. Check its status below; if paused, use Start or Resume to let this app use it again.",
                    NameDisplay, _error)
                : UiText.Get("ManagedSession_StopStartFailed",
                    "The session action for {0} failed: {1}. Check the session status and try again.", NameDisplay, _error)
        : !_finished ? string.Empty
        : _starting
            ? UiText.Get("ManagedSession_DefaultResumed", "The default CLI session is available again.")
        : _defaultSession
            ? UiText.Get("ManagedSession_DefaultStopped",
                "The stop request for the default CLI session finished. Its data is kept. Use Start or Resume to let this app use it again. Another app may start it independently. Reopening this app may also start it and containers configured for automatic start.")
            : UiText.Get("ManagedSession_StoppedProgress",
                "Stop requested for {0}. Its data is kept. Another app may start it again.", NameDisplay);

    public bool TryBegin(string name, bool defaultSession = false, bool starting = false)
    {
        if (_busy)
            return false;
        _name = name;
        _defaultSession = defaultSession;
        _starting = starting;
        _error = null;
        _finished = false;
        _busy = true;
        Refresh();
        return true;
    }

    public void Complete(string? error = null)
    {
        _error = error;
        _finished = true;
        _busy = false;
        Refresh();
    }

    public void RefreshLanguage() => Refresh();
    public void ClearCompleted()
    {
        if (_busy) return;
        _error = null;
        _finished = false;
        Refresh();
    }
    private void Refresh()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsSuccess));
        OnPropertyChanged(nameof(IsFailure));
        OnPropertyChanged(nameof(Message));
    }
}
