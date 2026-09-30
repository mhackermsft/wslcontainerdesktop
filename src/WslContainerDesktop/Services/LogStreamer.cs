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
using Microsoft.UI.Dispatching;
using WslContainerDesktop.Helpers;

namespace WslContainerDesktop.Services;

/// <summary>
/// Streams `wslc logs -f` output for a single container, raising <see cref="LineReceived"/>
/// on the UI thread for each line. Only one stream is active at a time; starting a new
/// one stops the previous. Safe to Stop/Dispose repeatedly.
/// </summary>
/// <remarks>
/// The engine writes stdout and stderr on separate pipes, so the replayed backlog arrives mixed
/// up. The stream therefore always asks for timestamps, holds the backlog until it goes quiet,
/// sorts it by time (see <see cref="LogLineOrder"/>), and removes the timestamps again unless the
/// caller asked to show them. Lines that arrive after the backlog are passed straight through.
/// </remarks>
public sealed class LogStreamer : IDisposable
{
    private readonly ISettingsService _settings;
    private readonly DispatcherQueue _dispatcher;

    // How long the backlog must be quiet before it is sorted and shown, and the longest we wait
    // for a chatty container before showing it anyway.
    private static readonly TimeSpan BacklogQuietPeriod = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BacklogMaxWait = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private Process? _process;
    private string? _currentId;

    // Identifies the current stream so late lines from a stopped process are ignored.
    private int _generation;
    private List<string>? _backlog;
    private Timer? _backlogTimer;
    private DateTime _backlogStartedUtc;
    private bool _showTimestamps;

    /// <summary>
    /// Gets line received for other services or view models.
    /// </summary>
    public event Action<string>? LineReceived;
    /// <summary>
    /// Stops ed work requested by the UI or a supervisor.
    /// </summary>
    public event Action? Stopped;

    /// <summary>
    /// Initializes a new <c>LogStreamer</c> with the collaborators it needs from dependency injection.
    /// </summary>
    public LogStreamer(ISettingsService settings, DispatcherQueue dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Gets current container id for other services or view models.
    /// </summary>
    public string? CurrentContainerId => _currentId;

    /// <summary>
    /// Starts the background work requested by the UI or a supervisor.
    /// </summary>
    public void Start(string containerId, int tail = 200, bool details = false, bool timestamps = false)
    {
        Stop();

        _currentId = containerId;

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

        psi.ArgumentList.Add("logs");
        if (details)
        {
            psi.ArgumentList.Add("--details");
        }

        // Always request timestamps: they are needed to put the backlog in order and are
        // stripped again in Format when the caller did not ask to show them.
        psi.ArgumentList.Add("--timestamps");

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("--tail");
        psi.ArgumentList.Add(tail.ToString());
        psi.ArgumentList.Add(containerId);

        int generation;
        lock (_gate)
        {
            generation = ++_generation;
            _showTimestamps = timestamps;
            _backlog = new List<string>();
            _backlogStartedUtc = DateTime.UtcNow;
            _backlogTimer = new Timer(_ => FlushBacklog(generation), null, BacklogMaxWait, Timeout.InfiniteTimeSpan);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLine(generation, e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(generation, e.Data);
        process.Exited += (_, _) =>
        {
            FlushBacklog(generation);
            _dispatcher.TryEnqueue(() => Stopped?.Invoke());
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
        }
        catch (Exception ex)
        {
            FlushBacklog(generation);
            Emit($"[failed to stream logs: {ex.Message}]");
            process.Dispose();
            _process = null;
        }
    }

    // Called on thread-pool threads by both pipe readers.
    private void OnLine(int generation, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            if (_backlog is not null)
            {
                _backlog.Add(line);
                if (DateTime.UtcNow - _backlogStartedUtc < BacklogMaxWait)
                {
                    // Debounce: wait for the replay to go quiet before sorting it.
                    _backlogTimer?.Change(BacklogQuietPeriod, Timeout.InfiniteTimeSpan);
                }

                return;
            }

            Emit(Format(line));
        }
    }

    // Sorts and emits the held backlog once; afterwards lines stream straight through.
    private void FlushBacklog(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _backlog is null)
            {
                return;
            }

            var ordered = LogLineOrder.OrderByTimestamp(_backlog);
            _backlog = null;
            _backlogTimer?.Dispose();
            _backlogTimer = null;
            foreach (var line in ordered)
            {
                Emit(Format(line));
            }
        }
    }

    private string Format(string line) => _showTimestamps ? line : LogLineOrder.StripTimestamp(line);

    private void Emit(string? line)
    {
        if (line is null)
        {
            return;
        }

        _dispatcher.TryEnqueue(() => LineReceived?.Invoke(line));
    }

    /// <summary>
    /// Stops the background work requested by the UI or a supervisor.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _generation++;
            _backlog = null;
            _backlogTimer?.Dispose();
            _backlogTimer = null;
        }

        var process = _process;
        _process = null;
        _currentId = null;

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
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Releases long-lived resources owned by this service.
    /// </summary>
    public void Dispose() => Stop();
}
