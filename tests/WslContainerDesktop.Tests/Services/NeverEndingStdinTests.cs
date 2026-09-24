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

using System.IO.Pipes;
using System.Runtime.InteropServices;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InheritedStdinCollection
{
    // Swaps the process-wide stdin handle, so nothing else may start processes concurrently.
    public const string Name = "Inherited stdin (serial)";
}

/// <summary>
/// Reproduces issue #120's environment: the app's inherited stdin is a console nobody types into,
/// so a child that inherits it blocks forever on a prompt. The test host's own stdin is usually
/// already at EOF, which would mask the bug; here it is replaced by a pipe that never closes.
/// </summary>
[Collection(InheritedStdinCollection.Name)]
public sealed class NeverEndingStdinTests
{
    private const int StdInputHandle = -10;

    [WindowsFact]
    public async Task PromptingPrune_ReturnsEvenWhenInheritedStdinNeverEnds()
    {
        using var stub = new WslcStub();
        using var neverEnding = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var original = GetStdHandle(StdInputHandle);
        Assert.True(SetStdHandle(StdInputHandle, neverEnding.ClientSafePipeHandle.DangerousGetHandle()));
        try
        {
            // Control: the pre-fix path inherits stdin and must still be waiting at the prompt.
            using (var control = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    ProcessRunner.RunAtPathAsync(stub.Prompt, ["container", "prune"], control.Token));
            }

            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await ProcessRunner.RunNonInteractiveAtPathAsync(stub.Prompt, ["container", "prune"],
                TimeSpan.FromSeconds(20), bounded.Token);

            Assert.False(result.Success);
            Assert.Contains("DECLINED", result.StandardOutput);
        }
        finally
        {
            SetStdHandle(StdInputHandle, original);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int handle, IntPtr value);
}
