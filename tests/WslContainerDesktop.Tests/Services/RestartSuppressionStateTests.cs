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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class RestartSuppressionStateTests
{
    [Fact]
    public void StopDuringIdentityResolutionWins()
    {
        var state = new RestartSuppressionState();
        state.Suppress("app");
        var version = state.Version;
        state.Suppress("app");
        var token = state.CaptureExplicitStart("app", version);
        Assert.False(state.CompleteExplicitStart(token, true));
        Assert.True(state.IsSuppressed("app"));
    }

    [Theory]
    [InlineData("same-id", "same-id")]
    [InlineData("old-id", "new-id")]
    public async Task ExplicitStartResumesByNameWithoutEngineGeneration(string oldId, string newId)
    {
        var state = new RestartSuppressionState();
        var container = new ContainerInfo { Id = oldId, Name = "app", StateChangedAt = 0 };
        state.Suppress(container.Name);
        var result = await state.RunExplicitStartAsync(container.Name, _ =>
        {
            container.Id = newId;
            container.StateValue = (int)ContainerState.Running;
            return Task.FromResult(new CommandResult());
        });
        Assert.True(result.Success);
        Assert.Equal(0UL, container.StateChangedAt);
        Assert.False(state.IsSuppressed("app"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LaterManualStopWinsAgainstInFlightExplicitStart(bool initiallySuppressed)
    {
        var state = new RestartSuppressionState();
        if (initiallySuppressed) state.Suppress("app");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = state.RunExplicitStartAsync("app", _ =>
        {
            started.SetResult();
            return completion.Task;
        });
        await started.Task;
        state.Suppress("app");
        completion.SetResult(new CommandResult());
        await operation;
        Assert.True(state.IsSuppressed("app"));
    }

    [Fact]
    public async Task FailedStartDoesNotResume()
    {
        var state = new RestartSuppressionState();
        state.Suppress("app");
        var result = await state.RunExplicitStartAsync("app",
            _ => Task.FromResult(new CommandResult { ExitCode = 1 }));
        Assert.False(result.Success);
        Assert.True(state.IsSuppressed("app"));
    }

    [Fact]
    public async Task CancellationDoesNotResume()
    {
        var state = new RestartSuppressionState();
        state.Suppress("app");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.RunExplicitStartAsync("app",
            ct => Task.FromCanceled<CommandResult>(ct), cancelled.Token));
        Assert.True(state.IsSuppressed("app"));
    }

    [Fact]
    public async Task ThrownOperationDoesNotResume()
    {
        var state = new RestartSuppressionState();
        state.Suppress("app");
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.RunExplicitStartAsync("app",
            _ => throw new InvalidOperationException("start failed")));
        Assert.True(state.IsSuppressed("app"));
    }

    [Fact]
    public void TokenCannotClearAnotherContainerOrLaterStop()
    {
        var state = new RestartSuppressionState();
        state.Suppress("/app");
        state.Suppress("other");
        var token = state.CaptureExplicitStart("app");
        Assert.True(state.CompleteExplicitStart(token, true));
        Assert.True(state.IsSuppressed("other"));
        state.Suppress("app");
        Assert.False(state.CompleteExplicitStart(token, true));
        Assert.True(state.IsSuppressed("/app"));
    }

    [Fact]
    public void SupervisionDoesNotResumeWithoutAnExplicitStartCompletion()
    {
        var state = new RestartSuppressionState();
        state.Suppress("app");
        var beforeAutomaticStart = state.CaptureExplicitStart("app");
        state.Suppress("app");
        Assert.True(state.IsSuppressed("app"));
        // Even an obsolete completion cannot undo the newer manual stop.
        Assert.False(state.CompleteExplicitStart(beforeAutomaticStart, true));
        Assert.True(state.IsSuppressed("app"));
    }
}
