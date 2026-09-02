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

namespace WslContainerDesktop.Tests.Models;

public sealed class ImageInfoCompatibilityTests
{
    [Fact]
    public void ParseList_Wsl299Image_ParsesHumanSizeAndCreatedAt()
    {
        const string output =
            """
            {"Containers":"0","CreatedAt":"2026-08-17 12:29:08 -0400 EDT","CreatedSince":"2 weeks ago","Digest":"<none>","ID":"cf3e50b742c6","Repository":"mcr.microsoft.com/dotnet/sdk","SharedSize":"N/A","Size":"5.04GB","Tag":"10.0","UniqueSize":"N/A"}
            """;

        var image = Assert.Single(WslcJsonParser.ParseList<ImageInfo>(output));

        Assert.Equal("cf3e50b742c6", image.Id);
        Assert.Equal(5_040_000_000, image.Size);
        Assert.Equal(new DateTimeOffset(2026, 8, 17, 12, 29, 8, TimeSpan.FromHours(-4)), image.CreatedUtc);
    }

    [Fact]
    public void ParseList_LegacyImage_PreservesNumericFields()
    {
        const string output =
            """[{"Id":"sha256:legacy","Repository":"example/legacy","Tag":"latest","Created":1,"Size":2048}]""";

        var image = Assert.Single(WslcJsonParser.ParseList<ImageInfo>(output));

        Assert.Equal("sha256:legacy", image.Id);
        Assert.Equal(2048, image.Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1), image.CreatedUtc);
    }

    [Fact]
    public void ParseList_Wsl299Image_ParsesNumericTimezoneName()
    {
        const string output =
            """
            {"CreatedAt":"2026-08-17 12:29:08 +0545 +0545","ID":"numeric-zone","Repository":"example/image","Size":"1MB","Tag":"latest"}
            """;

        var image = Assert.Single(WslcJsonParser.ParseList<ImageInfo>(output));

        Assert.Equal(
            new DateTimeOffset(2026, 8, 17, 12, 29, 8, TimeSpan.FromMinutes(345)),
            image.CreatedUtc);
    }
}
