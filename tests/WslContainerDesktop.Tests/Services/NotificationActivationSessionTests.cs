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

using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class NotificationActivationSessionTests
{
    [Fact]
    public void NamedAndDefaultSessionsRetainTheirEngine()
    {
        var named = NotificationActivation.ParseOwner(@"C:\engine\wslc.exe", "named", "Work");
        var standard = NotificationActivation.ParseOwner(@"C:\engine\wslc.exe", "default", null);

        Assert.Equal("Work", named!.SessionName);
        Assert.Equal(@"C:\engine\wslc.exe", named.ExecutablePath);
        Assert.Null(standard!.SessionName);
        Assert.Equal(named.ExecutablePath, standard.ExecutablePath);
    }

    [Fact]
    public void ReviewAndContainerNotificationArgumentsRouteWithoutGuessing()
    {
        var review = NotificationActivation.FromArguments(new Dictionary<string, string> { ["page"] = "wsl" });
        var logs = NotificationActivation.FromArguments(new Dictionary<string, string>
        {
            ["page"] = "containers",
            ["action"] = "logs",
            ["id"] = "123456",
            ["engine"] = @"C:\Program Files\WSL\wslc.exe",
            ["session-kind"] = "named",
            ["session-name"] = "Work",
        });

        Assert.Equal("wsl", review.Page);
        Assert.Null(review.SessionOwner);
        Assert.Equal("containers", logs.Page);
        Assert.Equal("logs", logs.Action);
        Assert.Equal("123456", logs.TargetId);
        Assert.Equal("Work", logs.SessionOwner!.SessionName);
        Assert.Equal(@"C:\Program Files\WSL\wslc.exe", logs.SessionOwner.ExecutablePath);
    }

    [Theory]
    [InlineData(null, "default", null)]
    [InlineData("engine", null, null)]
    [InlineData("engine", "named", "")]
    [InlineData("engine", "default", "other")]
    public void MissingOrAmbiguousOwnershipIsNotGuessed(string? path, string? kind, string? name) =>
        Assert.Null(NotificationActivation.ParseOwner(path, kind, name));
}
