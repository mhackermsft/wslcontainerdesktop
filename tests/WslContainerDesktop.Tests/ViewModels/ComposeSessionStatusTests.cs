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

#if WINDOWS
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tests.Services;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.ViewModels;

public sealed class ComposeSessionStatusTests
{
    [Fact]
    public async Task RefreshReadsOnlyProjectOwnerSessions()
    {
        var fixture = new Fixture(
            [Session("alpha", 1), Session("beta", 2), Session("unused", 3)],
            [Project("one", "alpha"), Project("two", "beta")]);

        await fixture.ViewModel.RefreshAsync();

        Assert.Equal(["alpha", "beta"], fixture.Reads.Order(StringComparer.Ordinal));
        Assert.All(fixture.ViewModel.Projects, row => Assert.NotEqual("Status unavailable", row.StatusText));
        Assert.DoesNotContain("unavailable", fixture.ViewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingDefaultSessionIsNotStartedToDisplayProjectStatus()
    {
        var fixture = new Fixture([], [Project("default", null)]);

        await fixture.ViewModel.RefreshAsync();

        Assert.Empty(fixture.Reads);
        Assert.Equal("Status unavailable", Assert.Single(fixture.ViewModel.Projects).StatusText);
        Assert.Contains("status is unavailable", fixture.ViewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneUnavailableSessionDoesNotEraseOtherProjectStatus()
    {
        var fixture = new Fixture([Session("alpha", 1), Session("beta", 2)],
            [Project("one", "alpha"), Project("two", "beta")]) { FailSession = "beta" };

        await fixture.ViewModel.RefreshAsync();

        Assert.NotEqual("Status unavailable", fixture.ViewModel.Projects.Single(row => row.Name == "one").StatusText);
        Assert.Equal("Status unavailable", fixture.ViewModel.Projects.Single(row => row.Name == "two").StatusText);
        Assert.Contains("status is unavailable", fixture.ViewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static WslcSessionInfo Session(string name, int id) => new() { Name = name, ID = id, CreatorPid = id + 10 };
    private static ComposeProject Project(string name, string? session) =>
        new() { Name = name, SessionOwner = AutomationSessionOwner.Capture("engine", new(session)) };

    private sealed class Fixture
    {
        public ConcurrentBag<string> Reads { get; } = [];
        public string? FailSession { get; set; }
        public ComposeViewModel ViewModel { get; }
        private ComposeNetworkSupervisorTests.Fixture Supervisor { get; } = new();

        public Fixture(IReadOnlyList<WslcSessionInfo> sessions, IReadOnlyList<ComposeProject> projects)
        {
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
                {
                    Server = new WslcServerInfo { Sessions = sessions },
                }),
                _ => throw new InvalidOperationException("The unscoped client must not read a project session."),
            });
            IWslcService Client(WslcSessionInfo session) => NetworkTestProxy.Create<IWslcService>((method, _) =>
            {
                if (method.Name != nameof(IWslcService.ListContainersAsync))
                    throw new InvalidOperationException(method.Name);
                Reads.Add(session.Name);
                return session.Name == FailSession
                    ? Task.FromException<IReadOnlyList<ContainerInfo>>(new IOException("unavailable"))
                    : Task.FromResult<IReadOnlyList<ContainerInfo>>([]);
            });
            var reader = new WslcSessionInventoryReader(discovery, (session, _) => Client(session),
                () => "engine", NullLogger<WslcSessionInventoryReader>.Instance);
            var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name == "get_WslcPath"
                ? "engine" : throw new InvalidOperationException(method.Name));
            var store = NetworkTestProxy.Create<IComposeProjectStore>((method, _) => method.Name == nameof(IComposeProjectStore.GetAll)
                ? projects : throw new InvalidOperationException(method.Name));
            ViewModel = new ComposeViewModel(store, Supervisor.Supervisor, discovery, new DialogService(),
                new WslcSessionContext(), sessionReader: reader, settings: settings);
        }
    }
}
#endif
