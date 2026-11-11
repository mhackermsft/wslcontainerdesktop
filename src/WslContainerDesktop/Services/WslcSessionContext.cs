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

namespace WslContainerDesktop.Services;

/// <summary>Process-wide selection for the WSLC session used by container operations.</summary>
public sealed class WslcSessionContext
{
    private readonly object _gate = new();
    private string? _selectedSessionName;
    private int _selectionLeases;
    private long _revision;
    private readonly HashSet<string> _stoppedDefaultExecutables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A selection and revision captured atomically for an asynchronous observation.</summary>
    public readonly record struct SelectionSnapshot(WslcSessionTarget Target, long Revision);
    /// <summary>Discovered names together with the retained selection, including an unavailable selection.</summary>
    public readonly record struct DiscoveryChoices(string? SelectedName, IReadOnlyList<string> Names, bool SelectedMissing);

    /// <summary>Raised after the last in-flight operation releases its selection lease.</summary>
    public event EventHandler? SelectionUnlocked;
    /// <summary>Raised when the selection-lock state changes.</summary>
    public event EventHandler? SelectionLockChanged;
    /// <summary>Raised when the selected session changes.</summary>
    public event EventHandler? SelectionChanged;
    /// <summary>Raised after an engine-setting change invalidates prior observations.</summary>
    public event EventHandler? EngineSettingChanged;
    /// <summary>Raised when the app pauses or resumes the default session after an explicit stop.</summary>
    public event EventHandler? DefaultSessionStopChanged;

    /// <summary>Whether this app has paused its default-session operations for the captured engine.</summary>
    public bool IsDefaultSessionStopped(string executable, WslcSessionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
            return _stoppedDefaultExecutables.Contains(executable) && IsDefaultTarget(target.Name);
    }

    private static bool IsDefaultTarget(string? name) =>
        name is null
#if WINDOWS
        || string.Equals(name, WslcSessionActionGuard.ResolveDefaultSessionName(), StringComparison.Ordinal)
#endif
        ;

    /// <summary>Pauses default-session operations and invalidates in-flight observations.</summary>
    internal void PauseDefaultSession(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        lock (_gate)
        {
            var selectedDefault = IsDefaultTarget(_selectedSessionName);
            if (!_stoppedDefaultExecutables.Add(executable))
                throw new InvalidOperationException(UiText.Get("ManagedSession_OperationInProgress",
                    "A session action is already in progress."));
            if (selectedDefault)
                _revision++;
        }
        DefaultSessionStopChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Resumes default-session operations only for the engine that was paused.</summary>
    internal void ResumeDefaultSession(string executable)
    {
        lock (_gate)
        {
            var selectedDefault = IsDefaultTarget(_selectedSessionName);
            if (!_stoppedDefaultExecutables.Remove(executable))
                throw new InvalidOperationException(UiText.Get("ManagedSession_Changed",
                    "The session changed. Refresh and try again."));
            if (selectedDefault)
                _revision++;
        }
        DefaultSessionStopChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether an in-flight operation currently prevents selection changes.</summary>
    public bool IsSelectionLocked
    {
        get
        {
            lock (_gate)
                return _selectionLeases > 0;
        }
    }

    /// <summary>Gets the selected session name, or null to use the CLI default session.</summary>
    public string? SelectedSessionName
    {
        get
        {
            lock (_gate)
                return _selectedSessionName;
        }
    }

    /// <summary>Selects a named session, or null to use the CLI default session.</summary>
    public void Select(string? sessionName)
    {
        if (!TrySelect(sessionName))
            throw new InvalidOperationException(UiText.Get("WslcSession_SelectionBusy",
                "The session cannot be changed while a session operation is running."));
    }

    /// <summary>Attempts to change selection without disturbing an in-flight session operation.</summary>
    public bool TrySelect(string? sessionName)
    {
        var normalized = string.IsNullOrWhiteSpace(sessionName) ? null : sessionName;
        bool changed;
        lock (_gate)
        {
            if (_selectionLeases > 0 && !string.Equals(_selectedSessionName, normalized, StringComparison.Ordinal))
                return false;
            changed = !string.Equals(_selectedSessionName, normalized, StringComparison.Ordinal);
            if (changed)
                _revision++;
            _selectedSessionName = normalized;
        }
        if (changed)
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Prevents selection changes until an operation finishes; the lease is idempotent.</summary>
    public IDisposable LockSelection()
    {
        bool changed;
        lock (_gate)
        {
            changed = _selectionLeases == 0;
            _selectionLeases++;
        }
        return CompleteSelectionLease(changed);
    }

    /// <summary>Acquires a lease only if the captured selection and engine are still current.</summary>
    public IDisposable? TryLockSelection(SelectionSnapshot snapshot)
    {
        bool changed;
        lock (_gate)
        {
            if (snapshot.Target is null || _revision != snapshot.Revision ||
                !string.Equals(_selectedSessionName, snapshot.Target.Name, StringComparison.Ordinal))
                return null;
            changed = _selectionLeases == 0;
            _selectionLeases++;
        }
        return CompleteSelectionLease(changed);
    }

    private IDisposable CompleteSelectionLease(bool changed)
    {
        var lease = new SelectionLease(this);
        try
        {
            if (changed)
                SelectionLockChanged?.Invoke(this, EventArgs.Empty);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>Applies an engine-setting change only when no session operation is in flight.</summary>
    public bool TryApplyIfUnlocked(Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        lock (_gate)
        {
            if (_selectionLeases > 0)
                return false;
            apply();
            _revision++;
        }
        EngineSettingChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Captures the current selection so a later selection change cannot retarget an operation.</summary>
    public WslcSessionTarget Capture() => CaptureSnapshot().Target;

    /// <summary>Captures a versioned selection for rejecting stale observations.</summary>
    public SelectionSnapshot CaptureSnapshot()
    {
        lock (_gate)
            return new(new WslcSessionTarget(_selectedSessionName), _revision);
    }

    /// <summary>Keeps a missing selected session visible instead of retargeting the default session.</summary>
    public DiscoveryChoices ReconcileDiscovery(IEnumerable<string?> discoveredNames)
    {
        ArgumentNullException.ThrowIfNull(discoveredNames);
        var names = discoveredNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var selected = SelectedSessionName;
        var missing = selected is not null && !names.Contains(selected, StringComparer.Ordinal);
        if (missing)
            names.Add(selected!);
        names.Sort((left, right) =>
        {
            var result = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left, right);
        });
        return new(selected, names, missing);
    }

    /// <summary>Applies an observation only while its captured selection is still current.</summary>
    public bool TryApply(SelectionSnapshot snapshot, Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        lock (_gate)
        {
            if (snapshot.Target is null || _revision != snapshot.Revision ||
                !string.Equals(_selectedSessionName, snapshot.Target.Name, StringComparison.Ordinal))
                return false;
            apply();
            return true;
        }
    }

    /// <summary>Prepends the CLI session selector when an explicit session is selected.</summary>
    public IEnumerable<string> AddSessionSelector(IEnumerable<string> arguments) =>
        Capture().AddSessionSelector(arguments);

    private sealed class SelectionLease(WslcSessionContext owner) : IDisposable
    {
        private WslcSessionContext? _owner = owner;

        public void Dispose()
        {
            var context = Interlocked.Exchange(ref _owner, null);
            if (context is null)
                return;
            EventHandler? unlocked = null;
            EventHandler? changed = null;
            lock (context._gate)
            {
                context._selectionLeases--;
                if (context._selectionLeases == 0)
                {
                    unlocked = context.SelectionUnlocked;
                    changed = context.SelectionLockChanged;
                }
            }
            try
            {
                unlocked?.Invoke(context, EventArgs.Empty);
            }
            finally
            {
                changed?.Invoke(context, EventArgs.Empty);
            }
        }
    }
}
