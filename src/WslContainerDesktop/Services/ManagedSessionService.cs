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

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using WslContainerDesktop.Models;
using SdkSession = Microsoft.WSL.Containers.Session;
using SdkSessionSettings = Microsoft.WSL.Containers.SessionSettings;

namespace WslContainerDesktop.Services;

internal interface IManagedSessionHandle : IDisposable
{
    event Action? Terminated;
    void Start();
    void Terminate();
}

internal sealed class ManagedSdkSessionHandle : IManagedSessionHandle
{
    private readonly SdkSession _session;
    public event Action? Terminated;

    public ManagedSdkSessionHandle(string name, string path)
    {
        _session = new SdkSession(new SdkSessionSettings(name, path));
        _session.Terminated += _ => Terminated?.Invoke();
    }

    public void Start() => _session.Start();
    public void Terminate() => _session.Terminate();
    public void Dispose() => _session.Dispose();
}

/// <summary>Owns sessions created by the app and restores their saved storage on launch.</summary>
public sealed class ManagedSessionService : IDisposable
{
    private readonly ManagedSessionStore _store;
    private readonly WslcSessionInventoryReader _reader;
    private readonly IWslcService _discovery;
    private readonly Func<WslcSessionTarget, string, IWslcService> _createClient;
    private readonly Func<WslcSessionTarget, string, WslcSessionInfo?, IWslcService>? _createGenerationClient;
    private readonly Func<string, string, IManagedSessionHandle> _createHandle;
    private readonly ISettingsService _settings;
    private readonly WslcSessionContext _context;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<ManagedSessionService> _logger;
    private readonly SessionRemovalGuard? _removalGuard;
    private readonly Func<AutomationSessionOwner, CancellationToken, Task>? _forgetAutostart;
    private readonly Action<AutomationSessionOwner>? _suspendAutomation;
    private readonly Func<CancellationToken, Task>? _waitForSelectedPoll;
    private readonly Func<CancellationToken, Task>? _waitForAutostart;
    private readonly TimeSpan _stopQuiescenceTimeout;
    private readonly TimeSpan _stopCommandTimeout;
    private readonly Action<string> _deleteStorage;
    private readonly Func<AutomationSessionOwner, CancellationToken, Task>? _pauseAutostart;
    private readonly Func<AutomationSessionOwner, CancellationToken, Task>? _resumeAutostart;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _runningGate = new();
    private sealed class OwnedSession(IManagedSessionHandle session, string executable)
    {
        public IManagedSessionHandle Session { get; } = session;
        public string Executable { get; } = executable;
        public WslcSessionInfo? Generation { get; set; }
        public bool IntentionalStop { get; set; }
        public bool Terminated { get; set; }
    }

    private readonly Dictionary<Guid, OwnedSession> _running = [];
    private readonly Dictionary<Guid, (string Name, bool WasPending)> _removing = [];
    private readonly Dictionary<Guid, string> _failures = [];
    private string? _restoreFailure;
    private bool _disposed;

    /// <summary>Raised after session definitions, availability, or errors change.</summary>
    public event EventHandler? Changed;

    /// <summary>Confirms a removed session immediately without another inventory read.</summary>
    internal event Action<string, string, DateTimeOffset>? SessionRemoved;

    /// <summary>Creates the session owner on the UI thread.</summary>
#if !WSLCD_TEST_NO_DISPATCHER
    public ManagedSessionService(ManagedSessionStore store, WslcSessionInventoryReader reader,
        IWslcService discovery, WslcSessionClientFactory clients, ISettingsService settings,
        WslcSessionContext context, DispatcherQueue dispatcher, ILogger<ManagedSessionService> logger,
        SessionRemovalGuard removalGuard, ContainerAutostartService autostart,
        SessionAutomationCoordinator automation, StatusMonitor monitor)
        : this(store, reader, discovery, (target, executable) => clients.CreateForSessionControl(target, executable),
            settings, context, dispatcher, logger, (name, path) => new ManagedSdkSessionHandle(name, path),
            removalGuard, autostart.QuiesceAndForgetOwnerAsync, automation.SuspendForRemoval,
            waitForSelectedPoll: monitor.WaitForCurrentPollAsync, waitForAutostart: autostart.WaitForCurrentRestoreAsync,
            pauseAutostart: autostart.PauseOwnerAsync, resumeAutostart: async (owner, ct) =>
            {
                await autostart.ResumeOwnerAsync(owner, ct);
                monitor.RequestRefresh();
                monitor.RequestAutomationRefresh();
            }, createGenerationClient: clients.CreateForSessionControl)
    {
    }
#endif

    internal ManagedSessionService(ManagedSessionStore store, WslcSessionInventoryReader reader,
        IWslcService discovery, Func<WslcSessionTarget, string, IWslcService> createClient,
        ISettingsService settings, WslcSessionContext context, DispatcherQueue dispatcher,
        ILogger<ManagedSessionService> logger, Func<string, string, IManagedSessionHandle> createHandle,
        SessionRemovalGuard? removalGuard = null,
        Func<AutomationSessionOwner, CancellationToken, Task>? forgetAutostart = null,
        Action<AutomationSessionOwner>? suspendAutomation = null,
        Action<string>? deleteStorage = null,
        Func<CancellationToken, Task>? waitForSelectedPoll = null,
        Func<CancellationToken, Task>? waitForAutostart = null,
        TimeSpan? stopQuiescenceTimeout = null, TimeSpan? stopCommandTimeout = null,
        Func<AutomationSessionOwner, CancellationToken, Task>? pauseAutostart = null,
        Func<AutomationSessionOwner, CancellationToken, Task>? resumeAutostart = null,
        Func<WslcSessionTarget, string, WslcSessionInfo?, IWslcService>? createGenerationClient = null)
    {
        _store = store;
        _reader = reader;
        _discovery = discovery;
        _createClient = createClient;
        _createGenerationClient = createGenerationClient;
        _createHandle = createHandle;
        _settings = settings;
        _context = context;
        _dispatcher = dispatcher;
        _logger = logger;
        _removalGuard = removalGuard;
        _forgetAutostart = forgetAutostart;
        _suspendAutomation = suspendAutomation;
        _waitForSelectedPoll = waitForSelectedPoll;
        _waitForAutostart = waitForAutostart;
        _stopQuiescenceTimeout = stopQuiescenceTimeout ?? TimeSpan.FromSeconds(75);
        _stopCommandTimeout = stopCommandTimeout ?? TimeSpan.FromMinutes(2);
        _deleteStorage = deleteStorage ?? (path => Directory.Delete(path, recursive: true));
        _pauseAutostart = pauseAutostart;
        _resumeAutostart = resumeAutostart;
    }

    /// <summary>Saved app-created sessions.</summary>
    public IReadOnlyList<ManagedSessionDefinition> Sessions => _store.Snapshot();
    /// <summary>Returns the directory holding one app-created session's data.</summary>
    public string StoragePath(ManagedSessionDefinition definition) => _store.StoragePath(definition);
    /// <summary>True when saved definitions cannot safely be read.</summary>
    public bool HasLoadError => _store.HasLoadError;
    /// <summary>Whether a saved session needs attention.</summary>
    public bool HasProblems
    {
        get
        {
            bool failures;
            lock (_failures)
                failures = _restoreFailure is not null || _failures.Count > 0;
            var sessions = _store.Snapshot();
            Dictionary<Guid, (string Name, bool WasPending)> removing;
            lock (_runningGate)
                removing = new(_removing);
            return _store.HasLoadError || failures ||
                sessions.Any(session => session.PendingDeletion &&
                    (!removing.TryGetValue(session.Id, out var operation) || operation.WasPending)) ||
                _settings.HasLoadError && sessions.Count > 0;
        }
    }

    /// <summary>Whether this app is actively removing a saved session.</summary>
    public bool IsRemoving(ManagedSessionDefinition definition)
    {
        lock (_runningGate)
            return _removing.ContainsKey(definition.Id);
    }

    /// <summary>Whether a named session is currently being removed.</summary>
    public bool IsRemoving(string name)
    {
        lock (_runningGate)
            return _removing.Values.Any(session => string.Equals(session.Name, name, StringComparison.Ordinal));
    }

    internal bool IsIntentionallyStopped(string name) =>
        !string.IsNullOrWhiteSpace(_settings.WslcPath) &&
        _removalGuard?.IsIntentionallyStopped(
            AutomationSessionOwner.Capture(_settings.WslcPath, new(name))) == true;

    /// <summary>Whether this app currently owns the saved session instance.</summary>
    public bool IsRunning(ManagedSessionDefinition definition)
    {
        lock (_runningGate)
            return _running.TryGetValue(definition.Id, out var owner) &&
                owner.Generation is not null && !owner.Terminated;
    }

    /// <summary>Whether an observed generation is the instance this app started.</summary>
    public bool Owns(ManagedSessionDefinition definition, WslcSessionInfo observed)
    {
        lock (_runningGate)
            return _running.TryGetValue(definition.Id, out var owner) && !owner.Terminated &&
                owner.Generation is { } generation && generation.ID == observed.ID &&
                generation.CreatorPid == observed.CreatorPid &&
                string.Equals(generation.Name, observed.Name, StringComparison.Ordinal);
    }

    /// <summary>Returns the last failure for one session, if any.</summary>
    public string? FailureFor(Guid id)
    {
        lock (_failures)
            return _failures.GetValueOrDefault(id);
    }

    /// <summary>Returns an unexpected restore failure that affected the session list.</summary>
    public string? RestoreFailure
    {
        get { lock (_failures) return _restoreFailure; }
    }

    internal void ReportRestoreFailure(Exception error)
    {
        lock (_failures)
            _restoreFailure = error.Message;
        RaiseChanged();
    }

    /// <summary>Starts every saved session once when the app opens.</summary>
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        if (_store.HasLoadError || _settings.HasLoadError)
        {
            RaiseChanged();
            return;
        }
        foreach (var session in _store.Snapshot().Where(session => !session.PendingDeletion))
        {
            ct.ThrowIfCancellationRequested();
            try { await StartAsync(session, ct); }
            catch (Exception ex) when (IsSessionFailure(ex))
            {
                RecordFailure(session, ex);
            }
        }
    }

    /// <summary>Saves and starts a newly named session.</summary>
    public async Task<ManagedSessionDefinition> CreateAsync(string name, CancellationToken ct = default)
    {
        EnsureSettingsReadable();
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Any(char.IsControl))
            throw new InvalidOperationException(UiText.Get("ManagedSession_NameRequired", "Enter a valid session name."));
        var active = await _reader.DiscoverAsync(ct);
        if (active.Any(session => string.Equals(session.Name, name, StringComparison.Ordinal)) ||
            _store.Snapshot().Any(session => string.Equals(session.Name, name, StringComparison.Ordinal)))
            throw new InvalidOperationException(UiText.Get("ManagedSession_Duplicate",
                "A session with this name already exists."));
        var definition = new ManagedSessionDefinition(Guid.NewGuid(), name);
        _store.Add(definition);
        RaiseChanged();
        try { await StartAsync(definition, ct); }
        catch (Exception ex) when (IsSessionFailure(ex))
        {
            RecordFailure(definition, ex);
            throw new InvalidOperationException(UiText.Get("ManagedSession_SavedButNotStarted",
                "The session was saved but could not start. You can retry it from the session list. {0}", ex.Message), ex);
        }
        return _store.Snapshot().Single(item => item.Id == definition.Id);
    }

    /// <summary>Starts a saved session, refusing to adopt another process's session.</summary>
    public async Task StartAsync(ManagedSessionDefinition definition, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureSettingsReadable();
            if (!_store.Snapshot().Contains(definition))
                throw ChangedSession();
            if (definition.PendingDeletion)
                throw new InvalidOperationException(UiText.Get("ManagedSession_DeletePending",
                    "Session removal was interrupted. Remove it again to finish or keep its data."));
            lock (_runningGate)
                if (_running.ContainsKey(definition.Id))
                    return;
            var active = await _reader.DiscoverAsync(ct);
            if (active.Any(session => string.Equals(session.Name, definition.Name, StringComparison.Ordinal)))
                throw new InvalidOperationException(UiText.Get("ManagedSession_NameInUse",
                    "Another process is already using this session name. Stop it before starting this saved session."));
            var path = _store.StoragePath(definition);
            if (definition.StorageCreated && !Directory.Exists(path))
                throw new InvalidOperationException(UiText.Get("ManagedSession_StorageMissing",
                    "This session's saved data folder is missing. It was not replaced with an empty session."));
            if (!definition.StorageCreated)
            {
                Directory.CreateDirectory(path);
                definition = _store.MarkStorageCreated(definition);
                RaiseChanged();
            }
            var executable = _settings.WslcPath;
            var session = _createHandle(definition.Name, path);
            var owner = new OwnedSession(session, executable);
            var registered = false;
            lock (_runningGate)
            {
                if (!_disposed)
                {
                    _running.Add(definition.Id, owner);
                    registered = true;
                }
            }
            if (!registered)
            {
                session.Dispose();
                throw new ObjectDisposedException(nameof(ManagedSessionService));
            }
            try
            {
                session.Terminated += () => OnTerminated(definition, owner);
                await Task.Run(session.Start, ct);
                var sessions = await _reader.DiscoverAsync(ct);
                if (!string.Equals(executable, _settings.WslcPath, StringComparison.OrdinalIgnoreCase))
                    throw ChangedSession();
                var generation = sessions.SingleOrDefault(item =>
                    string.Equals(item.Name, definition.Name, StringComparison.Ordinal) &&
                    item.CreatorPid == Environment.ProcessId);
                if (generation is null)
                    throw ChangedSession();
                definition = _store.MarkEngine(definition, executable);
                lock (_runningGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    EnsureSettingsReadable();
                    if (owner.Terminated)
                        throw new InvalidOperationException(UiText.Get("ManagedSession_StoppedUnexpectedly",
                            "The session stopped unexpectedly. Start it again from the session list."));
                    owner.Generation = generation;
                    lock (_failures)
                        _failures.Remove(definition.Id);
                }
                _removalGuard?.ReleaseStoppedSession(
                    AutomationSessionOwner.Capture(executable, new(definition.Name)));
            }
            catch
            {
                lock (_runningGate)
                {
                    owner.IntentionalStop = true;
                    if (_running.TryGetValue(definition.Id, out var current) && ReferenceEquals(current, owner))
                        _running.Remove(definition.Id);
                }
                session.Dispose();
                throw;
            }
            RaiseChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts a saved session and keeps a visible failure if it cannot start.</summary>
    public async Task StartSavedAsync(ManagedSessionDefinition definition, CancellationToken ct = default)
    {
        try
        {
            await StartAsync(definition, ct);
            if (_resumeAutostart is not null)
                await _resumeAutostart(AutomationSessionOwner.Capture(_settings.WslcPath, new(definition.Name)), ct);
        }
        catch (Exception ex) when (IsSessionFailure(ex))
        {
            RecordFailure(definition, ex);
            throw;
        }
    }

    /// <summary>Stops an app-created session while retaining its definition and data.</summary>
    public async Task StopAsync(ManagedSessionDefinition definition, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureSettingsReadable();
            if (!_store.Snapshot().Contains(definition))
                throw ChangedSession();
            var owner = AutomationSessionOwner.Capture(_settings.WslcPath, new(definition.Name));
            WslcSessionInfo generation;
            lock (_runningGate)
                generation = _running.GetValueOrDefault(definition.Id)?.Generation ?? throw ChangedSession();
            _removalGuard?.BeginStop(owner, generation);
            try
            {
                _suspendAutomation?.Invoke(owner);
                using var quiescence = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quiescence.CancelAfter(_stopQuiescenceTimeout);
                if (_pauseAutostart is not null)
                    await _pauseAutostart(owner, quiescence.Token);
                await StopOwnedCoreAsync(definition, ct);
                _removalGuard?.CompleteStop(owner);
            }
            catch
            {
                _removalGuard?.CancelStop(owner);
                RaiseChanged();
                throw;
            }
            RaiseChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes an app-created session and optionally deletes only its app-owned storage.</summary>
    public async Task<string> RemoveAsync(ManagedSessionDefinition definition, bool deleteData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await _gate.WaitAsync(ct);
        lock (_runningGate)
            _removing.Add(definition.Id, (definition.Name, definition.PendingDeletion));
        AutomationSessionOwner? removingOwner = null;
        var fenced = false;
        var completed = false;
        try
        {
            RaiseChanged();
            using var selection = _context.LockSelection();
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureSettingsReadable();
            if (!_store.Snapshot().Contains(definition))
                throw ChangedSession();
            var path = _store.StoragePath(definition);
            OwnedSession? owner;
            lock (_runningGate)
                _running.TryGetValue(definition.Id, out owner);
            var active = await _reader.DiscoverAsync(ct);
            if (owner?.Generation is { } ownedGeneration)
                await WslcSessionActionGuard.VerifyAsync(_discovery, () => _settings.WslcPath,
                    ownedGeneration, owner.Executable, ct);
            var externalNameInUse = owner is null && active.Any(item =>
                string.Equals(item.Name, definition.Name, StringComparison.Ordinal));
            var executable = owner?.Executable ?? definition.LastExecutablePath;
            var automationOwner = definition.PendingRemovalOwner ??
                (externalNameInUse || string.IsNullOrWhiteSpace(executable) ||
                 !string.Equals(executable, _settings.WslcPath, StringComparison.OrdinalIgnoreCase) ? null :
                    AutomationSessionOwner.Capture(executable, new WslcSessionTarget(definition.Name)));
            if (externalNameInUse && automationOwner is not null)
                throw new InvalidOperationException(UiText.Get("ManagedSession_DataInUse",
                    "Another process is using this session name. Its saved data was not deleted."));
            var generation = owner?.Generation;
            var removal = definition.PendingDeletion && automationOwner == definition.PendingRemovalOwner
                ? definition : _store.MarkForDeletion(definition, automationOwner);
            RaiseChanged();
            if (automationOwner is not null)
            {
                _removalGuard?.ResumeRemoval(automationOwner, generation);
                removingOwner = automationOwner;
                fenced = true;
                _suspendAutomation?.Invoke(automationOwner);
                if (_forgetAutostart is not null)
                    await _forgetAutostart(automationOwner, ct);
            }
            if (owner is not null)
                await StopOwnedCoreAsync(removal, ct);
            if (deleteData && (await _reader.DiscoverAsync(ct)).Any(item =>
                    string.Equals(item.Name, definition.Name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(UiText.Get("ManagedSession_DataInUse",
                    "Another process is using this session name. Its saved data was not deleted."));
            }
            lock (_failures)
                _failures.Remove(definition.Id);
            RaiseChanged();
            if (deleteData && Directory.Exists(path))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Run(() =>
                {
                    EnsureSafeStorage(path);
                    _deleteStorage(path);
                });
            }
            _store.Remove(removal);
            if (automationOwner is not null)
                _removalGuard?.Complete(automationOwner, generation);
            completed = true;
            var removedAt = DateTimeOffset.UtcNow;
            var removalExecutable = _settings.WslcPath;
            RaiseChanged(!externalNameInUse
                ? () => SessionRemoved?.Invoke(definition.Name, removalExecutable, removedAt)
                : null);
            return path;
        }
        finally
        {
            if (fenced && !completed && removingOwner is not null)
                _removalGuard?.EndRemovalAttempt(removingOwner);
            lock (_runningGate)
                _removing.Remove(definition.Id);
            _gate.Release();
            RaiseChanged();
        }
    }

    private async Task StopOwnedCoreAsync(ManagedSessionDefinition definition, CancellationToken ct)
    {
        OwnedSession owner;
        WslcSessionInfo generation;
        lock (_runningGate)
        {
            if (!_running.TryGetValue(definition.Id, out var current) || current.Generation is not { } observed)
                throw ChangedSession();
            owner = current;
            generation = observed;
        }
        await WslcSessionActionGuard.VerifyAsync(_discovery, () => _settings.WslcPath,
            generation, owner.Executable, ct);
        lock (_runningGate)
        {
            if (owner.Terminated || !_running.TryGetValue(definition.Id, out var current) ||
                !ReferenceEquals(current, owner))
                throw ChangedSession();
            owner.IntentionalStop = true;
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            await Task.Run(owner.Session.Terminate);
            lock (_runningGate)
                _running.Remove(definition.Id);
            owner.Session.Dispose();
        }
        catch
        {
            bool terminated;
            lock (_runningGate)
            {
                terminated = owner.Terminated;
                if (terminated)
                    _running.Remove(definition.Id);
                else
                    owner.IntentionalStop = false;
            }
            if (terminated)
            {
                owner.Session.Dispose();
                RecordFailure(definition, new InvalidOperationException(UiText.Get("ManagedSession_StoppedUnexpectedly",
                    "The session stopped unexpectedly. Start it again from the session list.")));
            }
            throw;
        }
    }

    /// <summary>Stops a session created by another process without deleting its data.</summary>
    public async Task StopExternalAsync(WslcSessionInfo session, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var selection = _context.LockSelection();
            EnsureSettingsReadable();
            if (_store.Snapshot().Any(definition =>
                    string.Equals(definition.Name, session.Name, StringComparison.Ordinal) && IsRunning(definition)))
                throw ChangedSession();
            var executable = _settings.WslcPath;
            await WslcSessionActionGuard.VerifyAsync(_discovery, () => _settings.WslcPath, session, executable, ct);
            var isDefault = string.Equals(session.Name, WslcSessionActionGuard.ResolveDefaultSessionName(), StringComparison.Ordinal);
            var wasAlreadyPaused = isDefault &&
                _context.IsDefaultSessionStopped(executable, WslcSessionTarget.Default);
            var owner = AutomationSessionOwner.Capture(executable, WslcSessionTarget.Default);
            var namedOwner = AutomationSessionOwner.Capture(executable, new WslcSessionTarget(session.Name));
            var fenced = false;
            var namedFenced = false;
            var paused = false;
            var commandStarted = false;
            try
            {
                _removalGuard?.BeginStop(namedOwner, session);
                namedFenced = _removalGuard is not null;
                _suspendAutomation?.Invoke(namedOwner);
                if (isDefault)
                {
                    _removalGuard?.BeginStop(owner, session);
                    fenced = _removalGuard is not null;
                    if (!wasAlreadyPaused)
                    {
                        _context.PauseDefaultSession(executable);
                        paused = true;
                    }
                    _suspendAutomation?.Invoke(owner);
                    _suspendAutomation?.Invoke(namedOwner);
                    using var quiescence = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    quiescence.CancelAfter(_stopQuiescenceTimeout);
                    try
                    {
                        if (_waitForAutostart is not null)
                            await _waitForAutostart(quiescence.Token);
                        if (_waitForSelectedPoll is not null &&
                            _context.IsDefaultSessionStopped(executable, _context.Capture()))
                            await _waitForSelectedPoll(quiescence.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && quiescence.IsCancellationRequested)
                    {
                        throw new TimeoutException(UiText.Get("ManagedSession_StopWaitTimedOut",
                            "Other session work did not finish in time. Wait for it to finish, then try stopping the session again."));
                    }
                    await WslcSessionActionGuard.VerifyAsync(_discovery, () => _settings.WslcPath, session, executable, ct);
                }
                using (var quiescence = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    quiescence.CancelAfter(_stopQuiescenceTimeout);
                    if (_pauseAutostart is not null)
                    {
                        await _pauseAutostart(namedOwner, quiescence.Token);
                        if (isDefault) await _pauseAutostart(owner, quiescence.Token);
                    }
                }
                var client = _createGenerationClient?.Invoke(new WslcSessionTarget(session.Name), executable, session)
                    ?? _createClient(new WslcSessionTarget(session.Name), executable);
                commandStarted = true;
                using var command = CancellationTokenSource.CreateLinkedTokenSource(ct);
                command.CancelAfter(_stopCommandTimeout);
                CommandResult result;
                try
                {
                    result = await client.RestartSessionAsync(command.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && command.IsCancellationRequested)
                {
                    throw new TimeoutException(UiText.Get("ManagedSession_StopCommandTimedOut",
                        "The stop request timed out, so the session's current state is unknown. Refresh the session list before trying again."));
                }
                if (!result.Success)
                    throw new InvalidOperationException(result.ErrorText);
                _removalGuard?.CompleteStop(namedOwner);
                RaiseChanged();
            }
            catch
            {
                if (commandStarted)
                {
                    _removalGuard?.CompleteStop(namedOwner);
                    if (fenced) _removalGuard?.CompleteStop(owner);
                }
                if (!commandStarted)
                {
                    if (fenced) _removalGuard!.CancelStop(owner);
                    if (namedFenced) _removalGuard!.CancelStop(namedOwner);
                    if (paused) _context.ResumeDefaultSession(executable);
                }
                RaiseChanged();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Starts or resumes the default session after an explicit stop in this app.</summary>
    public async Task ResumeDefaultAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var selection = _context.LockSelection();
            EnsureSettingsReadable();
            var executable = _settings.WslcPath;
            if (!_context.IsDefaultSessionStopped(executable, WslcSessionTarget.Default))
                throw ChangedSession();
            var defaultName = WslcSessionActionGuard.ResolveDefaultSessionName();
            var active = await _reader.DiscoverAsync(ct);
            var generation = active.SingleOrDefault(session => string.Equals(session.Name, defaultName, StringComparison.Ordinal));
            if (generation is null)
            {
                await _createClient(WslcSessionTarget.Default, executable).ListImagesAsync(ct);
                active = await _reader.DiscoverAsync(ct);
                generation = active.SingleOrDefault(session => string.Equals(session.Name, defaultName, StringComparison.Ordinal));
            }
            if (generation is null || generation.ID <= 0 || generation.CreatorPid <= 0)
                throw new InvalidOperationException(UiText.Get("SessionPicker_DefaultUnavailable",
                    "The default CLI session could not be started or identified. No container was created."));
            await WslcSessionActionGuard.VerifyAsync(_discovery, () => _settings.WslcPath, generation, executable, ct);
            _removalGuard?.ReleaseStoppedSession(AutomationSessionOwner.Capture(executable, WslcSessionTarget.Default));
            _removalGuard?.ReleaseStoppedSession(AutomationSessionOwner.Capture(executable, new WslcSessionTarget(defaultName)));
            _context.ResumeDefaultSession(executable);
            if (_resumeAutostart is not null)
            {
                await _resumeAutostart(AutomationSessionOwner.Capture(executable, WslcSessionTarget.Default), ct);
                await _resumeAutostart(AutomationSessionOwner.Capture(executable, new(defaultName)), ct);
            }
            RaiseChanged();
        }
        finally { _gate.Release(); }
    }

    private void EnsureSafeStorage(string path)
    {
        foreach (var directory in new[] { _store.Root, Path.Combine(_store.Root, "storage"), path })
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(UiText.Get("ManagedSession_UnsafeStorage",
                    "The session data contains a link and was not deleted."));
        }
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(UiText.Get("ManagedSession_UnsafeStorage",
                        "The session data contains a link and was not deleted."));
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
    }

    private static bool IsSessionFailure(Exception ex) =>
        ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or
            System.Text.Json.JsonException or
            System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or TimeoutException;

    private void OnTerminated(ManagedSessionDefinition definition, OwnedSession owner)
    {
        var error = new InvalidOperationException(UiText.Get("ManagedSession_StoppedUnexpectedly",
            "The session stopped unexpectedly. Start it again from the session list."));
        lock (_runningGate)
        {
            owner.Terminated = true;
            if (_disposed || owner.IntentionalStop ||
                !_running.TryGetValue(definition.Id, out var current) || !ReferenceEquals(current, owner))
                return;
            if (owner.Generation is null)
                return;
            _running.Remove(definition.Id);
            lock (_failures)
                _failures[definition.Id] = error.Message;
        }
        _logger.LogWarning(error, "App-created session {Session} stopped unexpectedly.", definition.Name);
        RaiseChanged();
        _ = Task.Run(() =>
        {
            try { owner.Session.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not release a terminated session handle."); }
        });
    }

    private void RecordFailure(ManagedSessionDefinition definition, Exception ex)
    {
        _logger.LogWarning(ex, "Could not start app-created session {Session}.", definition.Name);
        lock (_failures)
            _failures[definition.Id] = ex.Message;
        RaiseChanged();
    }

    private void RaiseChanged(Action? beforeChanged = null)
    {
        void Notify()
        {
            beforeChanged?.Invoke();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        if (_dispatcher.HasThreadAccess)
            Notify();
        else if (!_dispatcher.TryEnqueue(() => Notify()))
            _logger.LogDebug("The UI dispatcher could not receive a session update.");
    }

    private static InvalidOperationException ChangedSession() =>
        new(UiText.Get("ManagedSession_Changed", "The session changed. Refresh and try again."));

    private void EnsureSettingsReadable()
    {
        if (_settings.HasLoadError)
            throw new InvalidOperationException(UiText.Get("ManagedSession_SettingsReadFailed",
                "Saved settings could not be read. Repair them and restart the app before managing sessions."));
    }

    /// <summary>Releases only sessions owned by this app.</summary>
    public void Dispose()
    {
        List<IManagedSessionHandle> sessions;
        lock (_runningGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var owner in _running.Values)
                owner.IntentionalStop = true;
            sessions = _running.Values.Where(owner => owner.Generation is not null)
                .Select(owner => owner.Session).ToList();
        }
        foreach (var session in sessions)
        {
            try { session.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not release a session handle during shutdown."); }
        }
    }
}
