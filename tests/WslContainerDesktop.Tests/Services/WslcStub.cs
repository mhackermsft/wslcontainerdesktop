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

namespace WslContainerDesktop.Tests.Services;

/// <summary>
/// Real child processes standing in for wslc.exe, so tests exercise actual stdin, exit-code and
/// timeout behavior instead of a mocked runner. Each script appends its argv to &lt;name&gt;.log.
/// </summary>
internal sealed class WslcStub : IDisposable
{
    private const string Log = ">>\"%~dpn0.log\" echo(%*\r\n";

    public WslcStub()
    {
        Directory = Path.Combine(Path.GetTempPath(), "wcd-wslc-stub-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Write("quiet", "echo ARGS=%*\r\nexit /b 0\r\n");
        // Mirrors WSLC 2.9.12+ prune: prompts unless --force is passed, and a declined prompt exits 0.
        Write("prompt",
            "echo ARGS=%*\r\n" +
            "echo(%* | findstr /c:\"--force\" >nul && (echo PRUNED& exit /b 0)\r\n" +
            ">&2 echo WARNING! This will remove all stopped containers.\r\n" +
            "set \"answer=\"\r\n" +
            "set /p \"answer=Are you sure you want to continue? [y/N] \"\r\n" +
            "if /i \"%answer%\"==\"y\" (echo ACCEPTED) else (echo DECLINED)\r\n" +
            "exit /b 0\r\n");
        Write("sleep", "ping -n 60 127.0.0.1 >nul\r\nexit /b 0\r\n");
        Write("fail", ">&2 echo Volume not found\r\nexit /b 1\r\n");
    }

    public string Directory { get; }
    public string Quiet => Script("quiet");
    public string Prompt => Script("prompt");
    public string Sleep => Script("sleep");
    public string Fail => Script("fail");

    /// <summary>Argument lines recorded for <paramref name="scriptPath"/>, one per invocation.</summary>
    public string[] Invocations(string scriptPath)
    {
        var log = Path.ChangeExtension(scriptPath, ".log");
        return File.Exists(log) ? File.ReadAllLines(log).Select(line => line.Trim()).ToArray() : [];
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A just-killed script may still hold its log for a moment; the temp folder is harmless.
        }
    }

    private string Script(string name) => Path.Combine(Directory, name + ".cmd");

    private void Write(string name, string body) =>
        File.WriteAllText(Script(name), "@echo off\r\n" + Log + body);
}
