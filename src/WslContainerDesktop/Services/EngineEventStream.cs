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

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
#if !WSLCD_TEST_NO_DISPATCHER
using Microsoft.UI.Dispatching;
#endif
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Owns the long-lived <c>wslc events</c> stream and reconnects when it exits.</summary>
public sealed class EngineEventStream : IEngineEventStream, IDisposable
{
    private const int Capacity = 500;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthyConnection = TimeSpan.FromSeconds(30);

    private readonly ISettingsService _settings;
    private readonly IWslRequirementService _requirements;
    private readonly Func<Action, bool> _enqueue;
    private readonly ILogger<EngineEventStream> _logger;
    private readonly object _gate = new();
    private readonly List<EngineEvent> _recent = new();
    private readonly HashSet<string> _delivered = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Process? _process;
    private DateTimeOffset? _lastEventTimestamp;
    private bool _isConnected;
    private bool _disposed;
    private string? _streamPath;
    private int _reconnectPromptly;

    /// <summary>Raised on the UI dispatcher when a new unique <c>wslc events</c> entry is parsed.</summary>
    public event EventHandler<EngineEvent>? EventReceived;
    /// <summary>Raised when the background event stream connects or disconnects.</summary>
    public event EventHandler<bool>? ConnectionChanged;

#if !WSLCD_TEST_NO_DISPATCHER
    /// <summary>Creates the stream using the WinUI dispatcher so observers update on the UI thread.</summary>
    public EngineEventStream(
        ISettingsService settings,
        IWslRequirementService requirements,
        DispatcherQueue dispatcher,
        ILogger<EngineEventStream> logger)
        : this(settings, requirements, action => dispatcher.TryEnqueue(() => action()), logger)
    {
    }
#endif

    /// <summary>Creates the stream with an injected enqueue delegate, primarily for tests.</summary>
    internal EngineEventStream(
        ISettingsService settings,
        IWslRequirementService requirements,
        Func<Action, bool> enqueue,
        ILogger<EngineEventStream> logger)
    {
        _settings = settings;
        _requirements = requirements;
        _enqueue = enqueue;
        _logger = logger;
        _streamPath = settings.WslcPath;
        _settings.Changed += OnSettingsChanged;
        _requirements.Changed += OnRequirementChanged;
    }

    /// <summary>Whether the current <c>wslc events</c> process is connected.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _isConnected;
            }
        }
    }

    /// <summary>Most recent unique engine events, newest first, kept for UI display.</summary>
    public IReadOnlyList<EngineEvent> RecentEvents
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }
    }

    /// <summary>Starts the reconnecting background loop if it is not already running.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _disposed)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    /// <summary>Stops the background loop and kills the current <c>wslc events</c> process.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        KillCurrentProcess();
        SetConnected(false);
        cts?.Dispose();
    }

    /// <summary>Forces the stream to reconnect promptly, for example after settings change.</summary>
    public void Restart()
    {
        var shouldStart = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            shouldStart = _loop is null;
        }

        if (shouldStart)
        {
            Start();
            return;
        }

        Interlocked.Exchange(ref _reconnectPromptly, 1);
        KillCurrentProcess();
        SetConnected(false);
    }

    /// <summary>Reconnects <c>wslc events</c> with bounded backoff while WSL requirements are healthy.</summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        var backoff = InitialBackoff;
        while (!ct.IsCancellationRequested)
        {
            if (_requirements.Current.State != WslRequirementState.Ok)
            {
                SetConnected(false);
                await DelayAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                continue;
            }

            var started = DateTimeOffset.UtcNow;
            var exitCode = await RunOnceAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Back off only for repeated quick failures: a connection that stayed up, or one the app
            // ended on purpose (engine path change, session restart), reconnects promptly.
            if (Interlocked.Exchange(ref _reconnectPromptly, 0) == 1 ||
                DateTimeOffset.UtcNow - started >= HealthyConnection)
            {
                backoff = InitialBackoff;
            }

            SetConnected(false);
            _logger.LogDebug("wslc events stream exited with code {ExitCode}; reconnecting in {Delay}.", exitCode, backoff);
            await DelayAsync(backoff, ct).ConfigureAwait(false);
            backoff = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
        }
    }

    /// <summary>Runs one <c>wslc events</c> process and returns its exit code.</summary>
    private async Task<int?> RunOnceAsync(CancellationToken ct)
    {
        var process = CreateProcess();
        try
        {
            if (!process.Start())
            {
                return null;
            }

            ChildProcessJob.Shared?.TryAssign(process);
            lock (_gate)
            {
                _process = process;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            SetConnected(true);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "wslc events stream failed.");
            return null;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore
            }

            process.Dispose();
        }
    }

    /// <summary>Builds the event-stream process with argument-list escaping and UTF-8 output readers.</summary>
    private Process CreateProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _settings.WslcPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("events");
        if (_lastEventTimestamp is { } last)
        {
            psi.ArgumentList.Add("--since");
            psi.ArgumentList.Add(last.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.LogDebug("wslc events: {Line}", e.Data);
            }
        };
        return process;
    }

    /// <summary>Parses one event line, de-duplicates it, stores it, and notifies observers.</summary>
    private void OnLine(string? line)
    {
        if (!WslcEventParser.TryParseLine(line, out var evt, _logger))
        {
            return;
        }

        var key = evt.StableKey;
        lock (_gate)
        {
            if (!_delivered.Add(key))
            {
                return;
            }

            _lastEventTimestamp = evt.Timestamp;
            _recent.Insert(0, evt);
            while (_recent.Count > Capacity)
            {
                _delivered.Remove(_recent[^1].StableKey);
                _recent.RemoveAt(_recent.Count - 1);
            }
        }

        _enqueue(() => EventReceived?.Invoke(this, evt));
    }

    /// <summary>Disconnects the stream when the configured WSL container engine is not usable.</summary>
    private void OnRequirementChanged(object? sender, WslRequirementStatus e)
    {
        if (e.State != WslRequirementState.Ok)
        {
            KillCurrentProcess();
            SetConnected(false);
        }
    }

    /// <summary>Restarts only when the configured <c>wslc.exe</c> path changes.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Settings are saved for many unrelated reasons; only a different engine path needs a new stream.
        var path = _settings.WslcPath;
        lock (_gate)
        {
            if (string.Equals(path, _streamPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _streamPath = path;
        }

        Interlocked.Exchange(ref _reconnectPromptly, 1);
        KillCurrentProcess();
        SetConnected(false);
    }

    /// <summary>Terminates the currently running event process, if any.</summary>
    private void KillCurrentProcess()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Updates connection state and raises the UI-thread change event.</summary>
    private void SetConnected(bool connected)
    {
        var changed = false;
        lock (_gate)
        {
            if (_isConnected != connected)
            {
                _isConnected = connected;
                changed = true;
            }
        }

        if (changed)
        {
            _enqueue(() => ConnectionChanged?.Invoke(this, connected));
        }
    }

    /// <summary>Waits for reconnect backoff while treating cancellation as normal shutdown.</summary>
    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Unsubscribes from settings and requirement events and stops the stream.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _requirements.Changed -= OnRequirementChanged;
        Stop();
    }
}
