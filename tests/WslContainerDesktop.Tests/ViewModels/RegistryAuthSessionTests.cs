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
using WslContainerDesktop.Tests.Services;
using Xunit;

namespace WslContainerDesktop.Tests.ViewModels;

[Collection("UiText")]
public sealed class RegistryAuthSessionTests
{
    [Fact]
    public void CatalogMessagesRelocalizeWithoutChangingExternalTextOrArguments()
    {
        UiText.Configure((key, args) => key == "Catalog_HttpError"
            ? string.Format(UiText.LanguageTag == "zh-Hans" ? "注册表错误（{0}）" : "Registry error ({0})", args)
            : key);
        try
        {
            UiText.SetLanguage("en-US");
            var message = CatalogResult.LocalizedFailure(CatalogStatus.Failed, "Catalog_HttpError",
                "Registry error ({0})", 403);
            var raw = CatalogResult.Fail(CatalogStatus.Failed, "external-text");
            Assert.Equal("Registry error (403)", message.Message);
            UiText.SetLanguage("zh-Hans");
            Assert.Equal("注册表错误（403）", message.Message);
            Assert.Equal("external-text", raw.Message);
        }
        finally
        {
            UiText.Configure(null);
            UiText.SetLanguage("en-US");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenRefreshValidatesAndUsesOnlyTheOwningSession(bool changed)
    {
        var calls = new List<string>();
        var registry = new RegistryEntry
        {
            Host = "example.azurecr.io", IsAzure = true, AzureAcrName = "example", SubscriptionId = "test",
        };
        var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
        {
            "get_Registries" => new List<RegistryEntry> { registry },
            _ => throw new InvalidOperationException(method.Name),
        });
        var azure = NetworkTestProxy.Create<IAzureCliService>((method, _) => method.Name switch
        {
            nameof(IAzureCliService.GetSignedInUserAsync) => Task.FromResult<string?>("test-user"),
            nameof(IAzureCliService.GetAcrTokenAsync) =>
                Task.FromResult<(string LoginServer, string Token)?>(("example.azurecr.io", "test-token")),
            _ => throw new InvalidOperationException(method.Name),
        });
        IWslcService Engine(string name) => NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.LoginRegistryAsync) => RecordLogin(name),
            _ => throw new InvalidOperationException(method.Name),
        });
        Task<CommandResult> RecordLogin(string name)
        {
            calls.Add(name);
            return Task.FromResult(new CommandResult());
        }
        var refresher = new RegistryAuthRefresher(Engine("default"), azure, settings,
            NullLogger<RegistryAuthRefresher>.Instance);
        Task Verify(CancellationToken _)
        {
            calls.Add("verify");
            return changed ? Task.FromException(new InvalidOperationException("Session changed.")) : Task.CompletedTask;
        }

        if (changed)
            await Assert.ThrowsAsync<InvalidOperationException>(() => refresher.EnsureFreshForReferenceAsync(
                "example.azurecr.io/app:latest", sessionClient: Engine("chosen"), verifyTarget: Verify));
        else
            await refresher.EnsureFreshForReferenceAsync("example.azurecr.io/app:latest",
                sessionClient: Engine("chosen"), verifyTarget: Verify);

        Assert.Equal(changed ? ["verify"] : ["verify", "chosen"], calls);
    }
}
