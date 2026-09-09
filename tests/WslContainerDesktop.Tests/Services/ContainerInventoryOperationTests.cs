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

using System.Text.Json;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ContainerInventoryOperationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPreflight_ReportsFailureWithoutRunningReplacementOrPrune(bool malformed)
    {
        var mutations = 0;
        Exception? reported = null;
        var completed = await ContainerInventoryOperation.RunAsync(async () =>
        {
            await Task.Yield();
            if (malformed)
                WslcJsonParser.ParseContainers("""{"Id":"valid"} {"Id":""}""");
            else
                throw new InvalidOperationException("Container list failed (exit 1).");
            mutations++;
        }, error =>
        {
            reported = error;
            return Task.CompletedTask;
        });

        Assert.False(completed);
        Assert.Equal(0, mutations);
        Assert.NotNull(reported);
        if (malformed)
            Assert.IsType<JsonException>(reported);
        else
            Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public async Task SuccessfulEmptyInventory_AllowsActionWithoutReportingFailure()
    {
        var mutations = 0;
        var completed = await ContainerInventoryOperation.RunAsync(() =>
        {
            Assert.Empty(WslcJsonParser.ParseContainers("[]"));
            mutations++;
            return Task.CompletedTask;
        }, _ => throw new Xunit.Sdk.XunitException("Unexpected error notification."));

        Assert.True(completed);
        Assert.Equal(1, mutations);
    }

    [Fact]
    public async Task FailedPostflight_DoesNotRepeatAlreadyCompletedCleanup()
    {
        var mutations = 0;
        var notifications = 0;
        var completed = await ContainerInventoryOperation.RunAsync(() =>
        {
            mutations++;
            WslcJsonParser.ParseContainers("""[{"Id":"valid"},null]""");
            return Task.CompletedTask;
        }, _ =>
        {
            notifications++;
            return Task.CompletedTask;
        });

        Assert.False(completed);
        Assert.Equal(1, mutations);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task CancellationAndUnexpectedErrors_AreNotConvertedToInventoryFailures()
    {
        foreach (var error in new Exception[] { new OperationCanceledException(), new NotSupportedException() })
        {
            var thrown = await Assert.ThrowsAnyAsync<Exception>(() =>
                ContainerInventoryOperation.RunAsync(() => Task.FromException(error),
                    _ => throw new Xunit.Sdk.XunitException("Unexpected error notification.")));
            Assert.Same(error, thrown);
        }
    }

    [Fact]
    public async Task NotificationFailure_IsNotSwallowed()
    {
        var notificationError = new InvalidOperationException("Dialog unavailable.");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerInventoryOperation.RunAsync(
                () => Task.FromException(new JsonException("Invalid inventory.")),
                _ => Task.FromException(notificationError)));
        Assert.Same(notificationError, thrown);
    }
}
