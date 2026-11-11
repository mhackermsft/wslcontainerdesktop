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

using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class WslcSessionChoicesTests
{
    [Fact]
    public void ChoicesKeepDefaultAliasAndCaseSensitiveNamedSessionsSeparate()
    {
        var defaultSession = new WslcSessionInfo { ID = 1, CreatorPid = 10, Name = "wslc-cli-user" };
        var lower = new WslcSessionInfo { ID = 2, CreatorPid = 20, Name = "work" };
        var upper = new WslcSessionInfo { ID = 3, CreatorPid = 30, Name = "Work" };

        var choices = WslcSessionChoices.Create([lower, defaultSession, upper],
            @"C:\wslc.exe", defaultSession.Name, "Default");

        Assert.Equal(3, choices.Count);
        Assert.Null(choices[0].Target.Name);
        Assert.Same(defaultSession, choices[0].Generation);
        Assert.Equal(new[] { "Work", "work" }, choices.Skip(1).Select(choice => choice.Target.Name));
        Assert.All(choices, choice => Assert.Equal(@"C:\wslc.exe", choice.ExecutablePath));
        Assert.Same(upper, choices[1].Generation);
        Assert.Same(lower, choices[2].Generation);
    }

    [Fact]
    public void DefaultIsOfferedWhenItIsNotRunning()
    {
        var choice = Assert.Single(WslcSessionChoices.Create([], @"C:\wslc.exe", "wslc-cli-user", "Default"));
        Assert.Null(choice.Target.Name);
        Assert.Null(choice.Generation);
    }

    [Fact]
    public void DefaultAliasWithDifferentCasingIsNotSilentlyTreatedAsAnotherSession()
    {
        var session = new WslcSessionInfo { ID = 1, CreatorPid = 10, Name = "WSLC-CLI-USER" };
        Assert.Throws<InvalidOperationException>(() =>
            WslcSessionChoices.Create([session], @"C:\wslc.exe", "wslc-cli-user", "Default"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wslc-cli-user")]
    [InlineData("work")]
    public async Task ZeroOrOneActiveSessionDoesNotPrompt(string? name)
    {
        var sessions = name is null
            ? Array.Empty<WslcSessionInfo>()
            : [new WslcSessionInfo { ID = 4, CreatorPid = 40, Name = name }];
        var choices = WslcSessionChoices.Create(sessions, @"C:\wslc.exe", "wslc-cli-user", "Default");
        var promptCalls = 0;

        var selected = await WslcSessionChoices.SelectAsync(choices, _ =>
        {
            promptCalls++;
            return Task.FromResult<WslcSessionSelection?>(null);
        });

        Assert.Equal(0, promptCalls);
        Assert.NotNull(selected);
        Assert.Equal(name is null or "wslc-cli-user" ? null : name, selected.Target.Name);
        Assert.Equal(@"C:\wslc.exe", selected.ExecutablePath);
        if (sessions.Length == 0)
            Assert.Null(selected.Generation);
        else
            Assert.Same(sessions[0], selected.Generation);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MultipleActiveSessionsPromptAndPreserveSelectionOrCancellation(bool cancel, bool firstIsDefault)
    {
        var first = new WslcSessionInfo { ID = 1, CreatorPid = 10, Name = firstIsDefault ? "wslc-cli-user" : "work" };
        var second = new WslcSessionInfo { ID = 2, CreatorPid = 20, Name = "Work" };
        var choices = WslcSessionChoices.Create([first, second], @"C:\wslc.exe", "wslc-cli-user", "Default");
        var promptCalls = 0;
        var wanted = choices.Single(choice => ReferenceEquals(choice.Generation, first));

        var selected = await WslcSessionChoices.SelectAsync(choices, displayed =>
        {
            promptCalls++;
            Assert.Same(choices, displayed);
            return Task.FromResult(cancel ? null : wanted);
        });

        Assert.Equal(1, promptCalls);
        if (cancel)
            Assert.Null(selected);
        else
            Assert.Same(wanted, selected);
    }
}
