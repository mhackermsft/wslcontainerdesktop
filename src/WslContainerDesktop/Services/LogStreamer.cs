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

namespace WslContainerDesktop.Services;

/// <summary>
/// Streams `wslc logs -f` output for a single container, raising <see cref="LineReceived"/>
/// on the UI thread for each line. Only one stream is active at a time; starting a new
/// one stops the previous. Safe to Stop/Dispose repeatedly.
/// </summary>
public sealed class LogStreamer : IDisposable
{
    private readonly ISettingsService _settings;
    private readonly DispatcherQueue _dispatcher;

    private Process? _process;
    private string? _currentId;

    public event Action<string>? LineReceived;
    public event Action? Stopped;

    public LogStreamer(ISettingsService settings, DispatcherQueue dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
    }

    public string? CurrentContainerId => _currentId;

    public void Start(string containerId, int tail = 200)
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
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("--tail");
        psi.ArgumentList.Add(tail.ToString());
        psi.ArgumentList.Add(containerId);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Emit(e.Data);
        process.ErrorDataReceived += (_, e) => Emit(e.Data);
        process.Exited += (_, _) => _dispatcher.TryEnqueue(() => Stopped?.Invoke());

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
        }
        catch (Exception ex)
        {
            Emit($"[failed to stream logs: {ex.Message}]");
            process.Dispose();
            _process = null;
        }
    }

    private void Emit(string? line)
    {
        if (line is null)
        {
            return;
        }

        _dispatcher.TryEnqueue(() => LineReceived?.Invoke(line));
    }

    public void Stop()
    {
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

    public void Dispose() => Stop();
}
