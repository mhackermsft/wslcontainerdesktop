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
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tests.Services;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.ViewModels;

[Collection("UiText")]
public sealed class RegistriesSessionChoiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginUsesChosenSessionOrRefusesChangedSession(bool changed)
    {
        var fixture = new Fixture();
        fixture.Dialogs.OnShowDialog = dialog =>
        {
            var login = Assert.IsType<RegistryLoginDialog>(dialog);
            Assert.Contains("second", login.Title.ToString());
            login.Username = "user";
            login.Password = "secret";
            fixture.Changed = changed;
            return ContentDialogResult.Primary;
        };
        await fixture.ViewModel.LoginCommand.ExecuteAsync(fixture.Registry);
        Assert.Equal(changed ? [] : ["second:login"], fixture.Calls);
        Assert.Equal("first", fixture.Context.SelectedSessionName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoutNamesChosenSessionAndDoesNotMutateAfterCancellation(bool cancel)
    {
        var fixture = new Fixture();
        fixture.Dialogs.ConfirmResult = !cancel;
        await fixture.ViewModel.LogoutCommand.ExecuteAsync(fixture.Registry);
        Assert.Contains("second", fixture.Dialogs.LastConfirmation);
        Assert.Contains("shared", fixture.Dialogs.LastConfirmation);
        Assert.Equal(cancel ? [] : ["second:logout"], fixture.Calls);
        Assert.Equal("first", fixture.Context.SelectedSessionName);
        Assert.Equal(nameof(IWslcSessionSelectionService.ChooseActiveAsync), fixture.LastChoiceMethod);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovingSharedRegistryDoesNotRequireSessionOrChangeCredentials(bool confirm)
    {
        var fixture = new Fixture { ChoiceError = new InvalidOperationException("No active sessions") };
        fixture.Dialogs.ConfirmResult = confirm;
        await fixture.ViewModel.LoadAsync();

        await fixture.ViewModel.RemoveCommand.ExecuteAsync(fixture.Registry);

        Assert.Equal(0, fixture.ChoiceCount);
        Assert.Empty(fixture.Calls);
        Assert.Contains("credentials will remain", fixture.Dialogs.LastConfirmation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(!confirm, fixture.ViewModel.Registries.Contains(fixture.Registry));
        Assert.Equal(!confirm, fixture.SavedRegistries.Contains(fixture.Registry));
    }

    [Fact]
    public async Task CancelledSessionSelectionDoesNotAskForCredentials()
    {
        var fixture = new Fixture { CancelChoice = true };
        await fixture.ViewModel.LoginCommand.ExecuteAsync(fixture.Registry);
        Assert.Empty(fixture.Calls);
        Assert.Equal(0, fixture.Dialogs.ConfirmCount);
    }

    [Fact]
    public async Task ExplicitStatusRefreshUsesTheChosenSession()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Registries.Add(fixture.Registry);
        await fixture.ViewModel.RefreshLoginStatesCommand.ExecuteAsync(null);
        Assert.Equal(["second:probe"], fixture.Calls);
        Assert.Contains("second", fixture.ViewModel.StatusMessageDisplay);
        Assert.Equal("first", fixture.Context.SelectedSessionName);
    }

    [Fact]
    public async Task StatusRefreshDoesNotPublishEvidenceFromAReplacedSession()
    {
        var fixture = new Fixture { ChangeDuringProbe = true };
        fixture.ViewModel.Registries.Add(fixture.Registry);
        await fixture.ViewModel.RefreshLoginStatesCommand.ExecuteAsync(null);
        Assert.Equal(RegistryLoginState.Unknown, fixture.Registry.LoginState);
        Assert.Contains("could not be checked", fixture.ViewModel.StatusMessageDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FileCredentialBackendDoesNotPretendDockerHubIsAnonymous()
    {
        var fixture = new Fixture();
        var hub = new RegistryEntry { Name = "Docker Hub", IsDefault = true };
        fixture.ViewModel.Registries.Add(hub);
        await fixture.ViewModel.RefreshLoginStatesCommand.ExecuteAsync(null);
        Assert.Equal(RegistryLoginState.Unknown, hub.LoginState);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task NewerStatusRefreshDoesNotPublishAnOlderResult()
    {
        var fixture = new Fixture { HoldProbe = true };
        await fixture.ViewModel.LoadAsync();
        var first = fixture.ViewModel.RefreshLoginStatesAsync();
        await fixture.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.HoldProbe = false;
        await fixture.ViewModel.RefreshLoginStatesAsync();
        fixture.ReleaseProbe.SetResult(RegistryLoginState.Anonymous);
        await first;
        Assert.Equal(RegistryLoginState.LoggedIn, fixture.Registry.LoginState);
        Assert.Contains("second", fixture.ViewModel.StatusMessageDisplay);
    }

    [Fact]
    public async Task LoadingRegistriesDoesNotChooseOrProbeASession()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.LoadAsync();
        Assert.Empty(fixture.Calls);
        Assert.Equal(0, fixture.ChoiceCount);
        Assert.Equal(RegistryLoginState.Unknown, fixture.Registry.LoginState);
        Assert.Contains("not been checked", fixture.ViewModel.StatusMessageDisplay);
    }

    [Fact]
    public async Task CancellingInitialChoiceLeavesStatusUnknownAndRelocalizes()
    {
        UiText.Configure((key, _) => key == "Registry_StatusNotChecked" && UiText.LanguageTag == "zh-Hans"
            ? "尚未检查" : key);
        try
        {
            UiText.SetLanguage("en-US");
            var fixture = new Fixture { CancelChoice = true };
            fixture.Registry.LoginState = RegistryLoginState.LoggedIn;
            await fixture.ViewModel.LoadAsync();
            await fixture.ViewModel.RefreshLoginStatesAsync();
            Assert.Equal(RegistryLoginState.Unknown, fixture.Registry.LoginState);
            Assert.Contains("not been checked", fixture.ViewModel.StatusMessageDisplay);
            UiText.SetLanguage("zh-Hans");
            Assert.Equal("尚未检查", fixture.ViewModel.StatusMessageDisplay);
            Assert.Empty(fixture.Calls);
        }
        finally
        {
            UiText.Configure(null);
            UiText.SetLanguage("en-US");
        }
    }

    [Fact]
    public async Task LeavingThePageCancelsAChooserBeforeItCanProbe()
    {
        var fixture = new Fixture { HoldChoice = true };
        await fixture.ViewModel.LoadAsync();
        var load = fixture.ViewModel.RefreshLoginStatesAsync();
        await fixture.ChoiceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.ViewModel.StopStatusRefresh();
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(fixture.Calls);
        Assert.Equal(RegistryLoginState.Unknown, fixture.Registry.LoginState);
    }

    [Fact]
    public async Task SessionDiscoveryFailureIsVisibleWithoutClaimingLoginStatus()
    {
        var fixture = new Fixture { ChoiceError = new InvalidOperationException("No active sessions") };
        fixture.Registry.LoginState = RegistryLoginState.LoggedIn;

        await fixture.ViewModel.LoadAsync();
        await fixture.ViewModel.RefreshLoginStatesAsync();

        Assert.Equal(RegistryLoginState.Unknown, fixture.Registry.LoginState);
        Assert.Contains("No active sessions", fixture.ViewModel.StatusMessageDisplay);
        Assert.Equal(0, fixture.Dialogs.ConfirmCount);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task NoActiveSessionGuidanceChangesLanguageWithoutCheckingAnotherSession()
    {
        UiText.Configure((key, args) => key switch
        {
            "SessionPicker_NoActive" => UiText.LanguageTag == "zh-Hans" ? "没有活动会话" : "No active sessions",
            "Registry_StatusCheckFailed" => string.Format(UiText.LanguageTag == "zh-Hans" ? "检查失败：{0}" : "Check failed: {0}", args),
            _ => key,
        });
        try
        {
            UiText.SetLanguage("en-US");
            var fixture = new Fixture { ChoiceError = new WslcSessionInventoryErrors.InventoryException(
                WslcSessionInventoryErrors.Kind.NoActiveSessions) };
            await fixture.ViewModel.LoadAsync();
            await fixture.ViewModel.RefreshLoginStatesAsync();
            Assert.Equal("Check failed: No active sessions", fixture.ViewModel.StatusMessageDisplay);
            UiText.SetLanguage("zh-Hans");
            Assert.Equal("检查失败：没有活动会话", fixture.ViewModel.StatusMessageDisplay);
            Assert.Empty(fixture.Calls);
        }
        finally
        {
            UiText.Configure(null);
            UiText.SetLanguage("en-US");
        }
    }

    private sealed class Fixture
    {
        public WslcSessionContext Context { get; } = new();
        public DialogService Dialogs { get; } = new();
        public RegistryEntry Registry { get; } = new() { Name = "Registry", Host = "registry.example" };
        public List<RegistryEntry> SavedRegistries { get; }
        public List<string> Calls { get; } = [];
        public bool CancelChoice { get; set; }
        public bool Changed { get; set; }
        public bool ChangeDuringProbe { get; set; }
        public bool HoldProbe { get; set; }
        public bool HoldChoice { get; set; }
        public int ChoiceCount { get; private set; }
        public string? LastChoiceMethod { get; private set; }
        public Exception? ChoiceError { get; set; }
        public TaskCompletionSource ChoiceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RegistryLoginState> ReleaseProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RegistriesViewModel ViewModel { get; }

        public Fixture()
        {
            Context.Select("first");
            SavedRegistries = [Registry];
            Dialogs.DismissMessage.SetResult();
            var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
            {
                "get_Registries" => SavedRegistries,
                "get_WslcPath" => "engine",
                nameof(ISettingsService.Save) => null,
                _ => throw new InvalidOperationException(method.Name),
            });
            var chosen = NetworkTestProxy.Create<IWslcService>((method, _) =>
            {
                if (method.Name == nameof(IWslcService.LoginRegistryAsync)) Calls.Add("second:login");
                else if (method.Name == nameof(IWslcService.LogoutRegistryAsync)) Calls.Add("second:logout");
                else if (method.Name == nameof(IWslcService.ProbeRegistryLoginAsync))
                {
                    Calls.Add("second:probe");
                    if (HoldProbe)
                    {
                        ProbeStarted.TrySetResult();
                        return ReleaseProbe.Task;
                    }
                    if (ChangeDuringProbe) Changed = true;
                    return Task.FromResult(RegistryLoginState.LoggedIn);
                }
                else throw new InvalidOperationException(method.Name);
                return Task.FromResult(new CommandResult());
            });
            var engine = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
                {
                    Server = new WslcServerInfo { Sessions = [new() { Name = "first", ID = 1, CreatorPid = 100 }] },
                }),
                _ => throw new InvalidOperationException(method.Name),
            });
            var azure = Unused<IAzureCliService>();
            var credentials = Unused<IRegistryCredentialStore>();
            var settingsFile = NetworkTestProxy.Create<IWslcSettingsFileService>((method, _) =>
                method.Name == nameof(IWslcSettingsFileService.ReadAsync)
                    ? Task.FromResult(new WslcSettingsFile { CredentialStore = "file" })
                    : throw new InvalidOperationException(method.Name));
            var selector = NetworkTestProxy.Create<IWslcSessionSelectionService>((method, args) => method.Name switch
            {
                nameof(IWslcSessionSelectionService.ChooseAsync) or nameof(IWslcSessionSelectionService.ChooseForLaterAsync) or
                nameof(IWslcSessionSelectionService.ChooseActiveAsync) => ChooseAsync(method.Name, (CancellationToken)args![0]!),
                nameof(IWslcSessionSelectionService.VerifyAsync) => Changed
                    ? Task.FromException(new InvalidOperationException("changed")) : Task.CompletedTask,
                nameof(IWslcSessionSelectionService.CreateClient) => chosen,
                _ => throw new InvalidOperationException(method.Name),
            });
            var policy = NetworkTestProxy.Create<IWslPolicyService>((method, _) =>
                method.Name == nameof(IWslPolicyService.GetPolicy)
                    ? new WslPolicySnapshot(true, true, new WslRegistryAllowlist(WslRegistryAllowlistState.Unrestricted, []))
                    : throw new InvalidOperationException(method.Name));
            ViewModel = new RegistriesViewModel(azure, settings, Dialogs,
                new RegistryAuthRefresher(engine, azure, settings, NullLogger<RegistryAuthRefresher>.Instance),
                credentials, policy, new RegistryLogoutService(engine, credentials, settingsFile),
                selector, NullLogger<RegistriesViewModel>.Instance, settingsFile);
        }

        private static T Unused<T>() where T : class =>
            NetworkTestProxy.Create<T>((method, _) => throw new InvalidOperationException(method.Name));

        private async Task<WslcSessionSelection?> ChooseAsync(string method, CancellationToken ct)
        {
            ChoiceCount++;
            LastChoiceMethod = method;
            if (ChoiceError is { } error)
                throw error;
            if (HoldChoice)
            {
                ChoiceStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return CancelChoice ? null : new(new("second"), "engine",
                new WslcSessionInfo { Name = "second", ID = 2, CreatorPid = 200 }, "second");
        }
    }
}
