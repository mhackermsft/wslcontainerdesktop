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

using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

[Collection("UiText")]
public sealed class LocalAiSessionCoordinatorTests
{
    [Fact]
    public async Task ChosenSessionIsFixedUntilScopeIsReleased()
    {
        var fixture = new Fixture();
        using (var scope = await fixture.Coordinator.OpenAsync())
        {
            Assert.NotNull(scope);
            Assert.Equal("second", scope.Target.Selection.Target.Name);
            Assert.Equal("first", fixture.Context.SelectedSessionName);
            Assert.False(fixture.Context.TrySelect("another"));
        }
        Assert.True(fixture.Context.TrySelect("another"));
    }

    [Fact]
    public async Task CancellationReleasesSelectionWithoutStartingAnything()
    {
        var fixture = new Fixture { CancelChoice = true };
        Assert.Null(await fixture.Coordinator.OpenAsync());
        Assert.False(fixture.Context.IsSelectionLocked);
        Assert.Equal(0, fixture.ClientRequests);
    }

    [Fact]
    public async Task RemovalDoesNotStartADormantDefaultSession()
    {
        var fixture = new Fixture { DormantChoice = true };
        Assert.Null(await fixture.Coordinator.OpenAsync(startDefault: false));
        Assert.Equal(0, fixture.ClientRequests);
        Assert.False(fixture.Context.IsSelectionLocked);
    }

    [Fact]
    public async Task LocalAiMutationsAreSerializedAcrossSessions()
    {
        var fixture = new Fixture();
        var first = await fixture.Coordinator.OpenAsync();
        Assert.NotNull(first);
        var secondTask = fixture.Coordinator.OpenAsync();
        Assert.False(secondTask.IsCompleted);
        first.Dispose();
        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(second);
        Assert.True(fixture.Context.IsSelectionLocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresenceIncludesOtherSessionsAndReportsPartialFailures(bool failFirst)
    {
        var fixture = new Fixture { FailFirst = failFirst };
        var presence = await fixture.Coordinator.ReadPresenceAsync();
        Assert.True(presence.Present);
        Assert.Equal(!failFirst, presence.Complete);
    }

    [Fact]
    public void OlderPresenceReadCannotReplaceANewerObservationOrSurviveAnOperation()
    {
        var tracker = new LocalAiPresenceTracker();
        var older = tracker.Begin();
        var second = tracker.Begin();

        Assert.True(tracker.IsCurrent(second));
        Assert.False(tracker.IsCurrent(older));
        tracker.Invalidate();
        Assert.False(tracker.IsCurrent(second));
    }

    private sealed class Fixture
    {
        private readonly WslcSessionInfo _first = new() { Name = "first", ID = 1, CreatorPid = 100 };
        private readonly WslcSessionInfo _second = new() { Name = "second", ID = 2, CreatorPid = 200 };
        public WslcSessionContext Context { get; } = new();
        public bool CancelChoice { get; set; }
        public bool DormantChoice { get; set; }
        public bool FailFirst { get; set; }
        public int ClientRequests { get; private set; }
        public LocalAiSessionCoordinator Coordinator { get; }

        public Fixture()
        {
            Context.Select("first");
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
                {
                    Server = new WslcServerInfo { Sessions = [_first, _second] },
                }),
                _ => throw new InvalidOperationException(method.Name),
            });
            IWslcService Client(WslcSessionInfo session) => NetworkTestProxy.Create<IWslcService>((method, _) =>
                method.Name == nameof(IWslcService.ListContainersAsync)
                    ? session == _first && FailFirst
                        ? Task.FromException<IReadOnlyList<ContainerInfo>>(new IOException("unavailable"))
                        : Task.FromResult<IReadOnlyList<ContainerInfo>>(session == _second
                            ? [new() { Name = "wslcd-ollama" }] : [])
                    : throw new InvalidOperationException(method.Name));
            var reader = new WslcSessionInventoryReader(discovery, (session, _) => Client(session), () => "engine",
                NullLogger<WslcSessionInventoryReader>.Instance);
            var selector = NetworkTestProxy.Create<IWslcSessionSelectionService>((method, _) => method.Name switch
            {
                nameof(IWslcSessionSelectionService.ChooseAsync) or nameof(IWslcSessionSelectionService.ChooseForLaterAsync) or
                nameof(IWslcSessionSelectionService.ChooseActiveAsync) =>
                    Task.FromResult<WslcSessionSelection?>(CancelChoice
                        ? null : new(new("second"), "engine", DormantChoice ? null : _second, "second")),
                nameof(IWslcSessionSelectionService.VerifyAsync) => Task.CompletedTask,
                nameof(IWslcSessionSelectionService.CreateClient) => ChosenClient(),
                _ => throw new InvalidOperationException(method.Name),
            });
            var dialogs = new DialogService();
            dialogs.DismissMessage.SetResult();
            Coordinator = new LocalAiSessionCoordinator(selector, reader, Context, dialogs,
                NetworkTestProxy.Create<IAiCapabilityService>((method, _) => throw new InvalidOperationException(method.Name)),
                NullLogger<LocalAiSetupService>.Instance, NullLogger<LocalAiSessionCoordinator>.Instance);
            IWslcService ChosenClient() { ClientRequests++; return Client(_second); }
        }
    }
}
