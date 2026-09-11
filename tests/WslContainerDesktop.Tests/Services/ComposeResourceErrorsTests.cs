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

/// <summary>
/// A resource that does not exist yet is the normal first deployment, not unreadable inventory.
/// Treating the two the same blocks every new project, so both forms the engine emits are covered.
/// </summary>
public class ComposeResourceErrorsTests
{
    // Verbatim from wslc 2.9.11.0: `wslc network inspect <absent>` / `wslc volume inspect <absent>`.
    private const string ActualNetworkText = "Network not found: 'openwebui-net'\n[]";
    private const string ActualVolumeText = "Volume not found: 'openwebui_openwebui-data'\n[]";

    [Fact]
    public void ActualEngineText_IsRecognizedAsMissing()
    {
        Assert.True(ComposeResourceErrors.IsNetworkNotFound(ActualNetworkText));
        Assert.True(ComposeResourceErrors.IsVolumeNotFound(ActualVolumeText));
        Assert.True(ComposeResourceErrors.IsNotFound("network", ActualNetworkText));
        Assert.True(ComposeResourceErrors.IsNotFound("volume", ActualVolumeText));
    }

    [Fact]
    public void SentinelForm_StaysRecognized()
    {
        Assert.True(ComposeResourceErrors.IsNetworkNotFound("WSLC_E_NETWORK_NOT_FOUND: no such network"));
        Assert.True(ComposeResourceErrors.IsVolumeNotFound("WSLC_E_VOLUME_NOT_FOUND: no such volume"));
    }

    [Theory]
    [InlineData("NETWORK NOT FOUND: 'x'")]
    [InlineData("network not found: 'x'")]
    public void MessageForm_IsCaseInsensitive(string text) =>
        Assert.True(ComposeResourceErrors.IsNetworkNotFound(text));

    /// <summary>An engine that could not answer must stay blocked rather than be read as "absent".</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("permission denied")]
    [InlineData("engine is not running")]
    [InlineData("connection refused")]
    public void OtherFailures_AreNotTreatedAsMissing(string? text)
    {
        Assert.False(ComposeResourceErrors.IsNetworkNotFound(text));
        Assert.False(ComposeResourceErrors.IsVolumeNotFound(text));
    }

    /// <summary>The two kinds must not answer for each other.</summary>
    [Fact]
    public void KindsDoNotCrossMatch()
    {
        Assert.False(ComposeResourceErrors.IsVolumeNotFound(ActualNetworkText));
        Assert.False(ComposeResourceErrors.IsNetworkNotFound(ActualVolumeText));
    }
}
