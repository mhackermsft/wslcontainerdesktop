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

using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using Xunit;

namespace WslContainerDesktop.Tests.Helpers;

public sealed class NetworkDisplayListTests
{
    [Fact]
    public void Create_WhenWslcReturnsDefaults_DoesNotDuplicateBridge()
    {
        var networks = new[]
        {
            new NetworkInfo { Id = "bridge-id", Name = "bridge", Driver = "bridge" },
            new NetworkInfo { Id = "host-id", Name = "host", Driver = "host" },
            new NetworkInfo { Id = "none-id", Name = "none", Driver = "null" },
            new NetworkInfo { Id = "custom-id", Name = "app", Driver = "bridge" },
        };

        var display = NetworkDisplayList.Create(networks);

        Assert.Equal(4, display.Count);
        Assert.Single(display, network => network.Name == "bridge");
        Assert.All(display.Where(network => network.Name is "bridge" or "host" or "none"),
            network => Assert.True(network.IsBuiltIn));
        Assert.False(Assert.Single(display, network => network.Name == "app").IsBuiltIn);
    }

    [Fact]
    public void Create_WhenWslcOmitsBridge_AddsFallbackBridge()
    {
        var display = NetworkDisplayList.Create(
            [new NetworkInfo { Id = "custom-id", Name = "app", Driver = "bridge" }]);

        var bridge = Assert.Single(display, network => network.Name == "bridge");
        Assert.True(bridge.IsBuiltIn);
        Assert.Null(bridge.Id);
    }
}
