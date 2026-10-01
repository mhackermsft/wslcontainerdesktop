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

using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>
/// Covers <c>wsl -l -v</c> parsing and the k3s host-distro decision (issue #124: wsl.exe's
/// "no installed distributions" prose was shown as distribution rows).
/// </summary>
public sealed class WslDistroListParserTests
{
    [Fact]
    public void ParsesTableWithDefaultMarkerSpacesAndBothVersions()
    {
        const string output =
            "  NAME              STATE           VERSION\r\n" +
            "* Ubuntu            Running         2\r\n" +
            "  Debian            Stopped         2\r\n" +
            "  Legacy Distro     Stopped         1\r\n";

        var distros = WslDistroListParser.Parse(output);

        Assert.Equal(3, distros.Count);
        Assert.Equal("Ubuntu", distros[0].Name);
        Assert.True(distros[0].IsDefault);
        Assert.True(distros[0].IsRunning);
        Assert.Equal(2, distros[0].Version);
        Assert.Equal("Debian", distros[1].Name);
        Assert.False(distros[1].IsDefault);
        Assert.Equal("Stopped", distros[1].State);
        Assert.Equal("Legacy Distro", distros[2].Name);
        Assert.Equal(1, distros[2].Version);
    }

    [Fact]
    public void NoInstalledDistributionsMessageYieldsNoRows()
    {
        const string output =
            "Windows Subsystem for Linux has no installed distributions.\r\n" +
            "You can resolve this by installing a distribution with the instructions below:\r\n" +
            "\r\n" +
            "Use 'wsl.exe --list --online' to list available distributions\r\n" +
            "and 'wsl.exe --install <Distro>' to install.\r\n";

        Assert.Empty(WslDistroListParser.Parse(output));
    }

    [Theory]
    [InlineData("  NAME      STATE           VERSION\n")]
    [InlineData("  NOMBRE    ESTADO          VERSIÓN\n")]
    [InlineData("")]
    public void HeaderOnlyOrEmptyOutputYieldsNoRows(string output)
    {
        Assert.Empty(WslDistroListParser.Parse(output));
    }

    [Fact]
    public void LocalizedHeaderIsNotTreatedAsADistro()
    {
        const string output =
            "  NAME      STATUS          VERSION\n" +
            "* Ubuntu    Beendet         2\n";

        var distro = Assert.Single(WslDistroListParser.Parse(output));
        Assert.Equal("Ubuntu", distro.Name);
        Assert.Equal("Beendet", distro.State);
        Assert.Equal(2, distro.Version);
    }
}
