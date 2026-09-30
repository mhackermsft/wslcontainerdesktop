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

using System.Text.Json;
using WslContainerDesktop.Models;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Covers parsing recorded <c>wslc</c> system information JSON while ignoring unknown fields from newer CLIs.</summary>
public sealed class WslcSystemInfoTests
{
    [Fact]
    public void ParsesRecordedSystemInfoJsonAndIgnoresUnknownFields()
    {
        const string json = """
            {"Client":{"Direct3DVersion":"1.611.1-81528511","DxCoreVersion":"10.0.26100.1-240331-1435.ge-release","KernelVersion":"6.18.40.1-1","SettingsFile":"C:\\Users\\mhacker\\AppData\\Local\\wslc\\settings.yaml","Version":"3.0.1.0","WindowsVersion":"10.0.26310.28132","Future":"ignored"},"Server":{"SessionManagerVersion":"3.0.1","Sessions":[{"CreatorPid":36456,"ID":1,"Name":"wslc-cli-mhacker"}]}}
            """;

        var info = JsonSerializer.Deserialize<WslcSystemInfo>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(info);
        Assert.Equal("3.0.1.0", info!.Client.Version);
        Assert.Equal(@"C:\Users\mhacker\AppData\Local\wslc\settings.yaml", info.Client.SettingsFile);
        Assert.Equal("3.0.1", info.Server.SessionManagerVersion);
        var session = Assert.Single(info.Server.Sessions);
        Assert.Equal(1, session.ID);
        Assert.Equal(36456, session.CreatorPid);
        Assert.Equal("wslc-cli-mhacker", session.Name);
    }
}
