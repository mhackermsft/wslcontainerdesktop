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
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Owns the lifecycle of active <c>kubectl port-forward</c> sessions. Each session is a long-lived
/// <c>wsl.exe</c> child process tracked by id; the manager keeps them drained (so the pipe doesn't
/// block) and tears them down individually or all at once on shutdown.
/// </summary>
public sealed class PortForwardManager(WslRootShell shell)
{
    private readonly Dictionary<string, Process> _portForwards = new();
    private readonly object _pfLock = new();

    public bool StartPortForward(PortForward forward)
    {
        var cmd = $"k3s kubectl port-forward --address 127.0.0.1 -n {WslRootShell.ShellEscape(forward.Namespace)} " +
                  $"{WslRootShell.ShellEscape(forward.TargetRef)} {forward.LocalPort}:{forward.RemotePort}";

        var psi = shell.BaseStartInfo(cmd);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return false;
            }

            // Drain output so the pipe doesn't fill and block the forward.
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };

            lock (_pfLock)
            {
                _portForwards[forward.Id] = process;
            }

            return true;
        }
        catch
        {
            process.Dispose();
            return false;
        }
    }

    public void StopPortForward(string id)
    {
        Process? process;
        lock (_pfLock)
        {
            if (!_portForwards.TryGetValue(id, out process))
            {
                return;
            }

            _portForwards.Remove(id);
        }

        KillProcessTree(process);
    }

    public void StopAllPortForwards()
    {
        List<Process> all;
        lock (_pfLock)
        {
            all = _portForwards.Values.ToList();
            _portForwards.Clear();
        }

        foreach (var p in all)
        {
            KillProcessTree(p);
        }
    }

    private static void KillProcessTree(Process process)
    {
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
}
