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
/// <c>wsl --version</c> labels are localized (and zh-TW uses a full-width colon), so the WSL and
/// kernel versions must be read by position, not by matching English labels.
/// </summary>
public sealed class WslVersionOutputParserTests
{
    // Label templates are WSL's own MessagePackageVersions strings for each locale.
    private static string Output(string wslLabel, string kernelLabel, string colon = ": ") =>
        $"{wslLabel}{colon}3.0.1.0\r\n" +
        $"{kernelLabel}{colon}6.18.40.1-1\r\n" +
        $"WSLg{colon}1.0.79\r\n" +
        $"MSRDC{colon}1.2.7214\r\n" +
        $"Direct3D{colon}1.611.1-81528511\r\n" +
        $"DXCore{colon}10.0.26100.1-240331-1435.ge-release\r\n" +
        $"Windows{colon}10.0.26310.28132\r\n";

    [Theory]
    [InlineData("WSL version", "Kernel version", ": ")]
    [InlineData("WSL-Version", "Kernelversion", ": ")]
    [InlineData("Version WSL", "Version du noyau", " : ")]
    [InlineData("WSL バージョン", "カーネル バージョン", ": ")]
    [InlineData("Версия WSL", "Версия ядра", ": ")]
    [InlineData("WSL 版本", "内核版本", ": ")]
    [InlineData("WSL 版本", "核心版本", "： ")]
    public void ReadsVersionsInEveryLanguage(string wslLabel, string kernelLabel, string colon)
    {
        var (wsl, kernel) = WslVersionOutputParser.Parse(Output(wslLabel, kernelLabel, colon));

        Assert.Equal("3.0.1.0", wsl);
        Assert.Equal("6.18.40.1-1", kernel);
    }

    [Fact]
    public void IgnoresALeadingLineThatIsNotAVersion()
    {
        var output = "wsl: Note: a localhost proxy configuration was detected\n" +
            Output("WSL version", "Kernel version");

        var (wsl, kernel) = WslVersionOutputParser.Parse(output);

        Assert.Equal("3.0.1.0", wsl);
        Assert.Equal("6.18.40.1-1", kernel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Windows Subsystem for Linux is not installed.")]
    public void UnrecognizedOutputYieldsEmptyVersions(string output)
    {
        Assert.Equal((string.Empty, string.Empty), WslVersionOutputParser.Parse(output));
    }
}
