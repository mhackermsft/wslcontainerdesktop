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
public sealed class AssistantSessionResolverTests
{
    [Fact]
    public async Task PullUsesChosenSessionEvenWhenManagementSelectionChanges()
    {
        var fixture = new Fixture();
        var resolved = await fixture.Resolve("pull_image", """{"reference":"example:latest"}""");
        Assert.Contains("second", resolved.Summary);
        fixture.Management.Select("third");
        await resolved.ExecuteAsync(default);
        Assert.Equal(["second:pull:example:latest"], fixture.Calls);
        Assert.Equal("third", fixture.Management.SelectedSessionName);
    }

    [Fact]
    public async Task ChangedSessionAfterApprovalDoesNotMutate()
    {
        var fixture = new Fixture();
        var resolved = await fixture.Resolve("pull_image", """{"reference":"example:latest"}""");
        fixture.SessionChanged = true;
        var result = await resolved.ExecuteAsync(default);
        Assert.Contains("no action was started", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledOrDormantChoiceDoesNotStartSessionOrMutate(bool cancelled)
    {
        var fixture = new Fixture { CancelChoice = cancelled, DormantChoice = !cancelled };
        var resolved = await fixture.Resolve("pull_image", """{"reference":"example:latest"}""");
        Assert.NotNull(resolved.BlockedResult);
        Assert.Empty(fixture.Calls);
        Assert.Equal(0, fixture.VerifyCalls);
    }

    [Fact]
    public async Task InvalidAndDisabledActionsDoNotPrompt()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Resolve("pull_image", "{}"));
        var blocked = await fixture.Resolve("remove_volume", """{"name":"data"}""");
        Assert.NotNull(blocked.BlockedResult);
        Assert.Equal(0, fixture.ChoiceCount);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task ExistingComposeProjectUsesItsOwnerWithoutPrompting()
    {
        var fixture = new Fixture();
        fixture.Project = new ComposeProject
        {
            Name = "demo",
            SessionOwner = AutomationSessionOwner.Capture("engine", new("second")),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Resolve("start_compose_project", """{"projectName":"demo"}"""));
        Assert.Equal("second", fixture.ScopeRequested);
        Assert.Equal(0, fixture.ChoiceCount);
    }

    [Fact]
    public async Task DeclinedTemplateReleasesItsTemporaryScopeOnlyOnce()
    {
        var fixture = new Fixture { ProvideScope = true };
        var resolved = await fixture.Resolve("deploy_template", """{"idOrName":"sample"}""");
        Assert.NotNull(resolved.DeclineAsync);
        await resolved.DeclineAsync!();
        await resolved.DeclineAsync!();
        Assert.Equal(1, fixture.ScopeDisposals);
        Assert.Empty(fixture.Calls);
    }

    private sealed class Fixture
    {
        private readonly WslcSessionInfo _first = new() { ID = 1, CreatorPid = 100, Name = "first" };
        private readonly WslcSessionInfo _second = new() { ID = 2, CreatorPid = 200, Name = "second" };
        private readonly AssistantToolset _tools;
        public WslcSessionContext Management { get; } = new();
        public List<string> Calls { get; } = [];
        public bool CancelChoice { get; set; }
        public bool DormantChoice { get; set; }
        public bool SessionChanged { get; set; }
        public bool ProvideScope { get; set; }
        public int ChoiceCount { get; private set; }
        public int VerifyCalls { get; private set; }
        public int ScopeDisposals { get; private set; }
        public string? ScopeRequested { get; private set; }
        public ComposeProject? Project { get; set; }

        public Fixture()
        {
            Management.Select("first");
            var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
            {
                "get_WslcPath" => "engine",
                "get_AiAssistantAllowDestructive" => false,
                "get_Registries" => new List<RegistryEntry>(),
                _ => throw new InvalidOperationException(method.Name),
            });
            var store = NetworkTestProxy.Create<IComposeProjectStore>((method, _) => method.Name switch
            {
                nameof(IComposeProjectStore.GetAll) => Project is null ? new List<ComposeProject>() : [Project],
                nameof(IComposeProjectStore.Get) => Project,
                _ => throw new InvalidOperationException(method.Name),
            });
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
                {
                    Server = new WslcServerInfo { Sessions = [_first, _second] },
                }),
                _ => throw new InvalidOperationException(method.Name),
            });
            var reader = new WslcSessionInventoryReader(discovery, (_, _) => Client(), () => "engine",
                NullLogger<WslcSessionInventoryReader>.Instance);
            var selector = NetworkTestProxy.Create<IWslcSessionSelectionService>((method, _) => method.Name switch
            {
                nameof(IWslcSessionSelectionService.VerifyAsync) => Verify(),
                nameof(IWslcSessionSelectionService.CreateClient) => Client(),
                _ => throw new InvalidOperationException(method.Name),
            });
            var resolver = new AssistantSessionResolver(selector, reader, store, settings, Management,
                null!, NullLogger<AssistantSessionResolver>.Instance, Choose, OpenScope);
            var templates = NetworkTestProxy.Create<ITemplateCatalog>((method, _) =>
                method.Name == "get_Templates"
                    ? new List<StackTemplate> { new() { Id = "sample", Name = "sample", Category = "test",
                        Description = "test", RunOptions = new() { Image = "example" } } }
                    : throw new InvalidOperationException(method.Name));
            _tools = new AssistantToolset(discovery, Unused<IKubernetesService>(), templates, store, null!,
                settings, Unused<IRegistryCatalogService>(), sessionContext: Management, sessionResolver: resolver);
        }

        public Task<AssistantResolvedToolCall> Resolve(string name, string args) =>
            _tools.ResolveAsync(AiContractHarness.Call(name, args), default);

        private Task<WslcSessionSelection?> Choose(CancellationToken _)
        {
            ChoiceCount++;
            return Task.FromResult<WslcSessionSelection?>(CancelChoice ? null : new(new("second"), "engine",
                DormantChoice ? null : _second, "second"));
        }

        private Task Verify()
        {
            VerifyCalls++;
            return SessionChanged ? Task.FromException(new InvalidOperationException("Session changed")) : Task.CompletedTask;
        }

        private Task<ComposeSessionScope> OpenScope(WslcSessionSelection destination, CancellationToken _)
        {
            ScopeRequested = destination.Target.Name;
            if (!ProvideScope) throw new InvalidOperationException("Scope requested");
            var context = new WslcSessionContext();
            context.Select(destination.Target.Name);
            return Task.FromResult(new ComposeSessionScope(destination, Client(), context, null!, default,
                new CallbackDisposable(() => ScopeDisposals++)));
        }

        private IWslcService Client() => NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
        {
            nameof(IWslcService.PullImageAsync) => Pull((string)args[0]!),
            _ => throw new InvalidOperationException(method.Name),
        });

        private Task<CommandResult> Pull(string reference)
        {
            Calls.Add($"second:pull:{reference}");
            return Task.FromResult(new CommandResult());
        }

        private static T Unused<T>() where T : class =>
            NetworkTestProxy.Create<T>((method, _) => throw new InvalidOperationException(method.Name));
    }

    private sealed class CallbackDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
