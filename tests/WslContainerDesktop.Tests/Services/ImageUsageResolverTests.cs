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

/// <summary>
/// Which container holds an image. Getting this wrong in the permissive direction offers space that
/// no prune can free, which is how a dangling row that cannot be removed looks like a page that
/// refuses to refresh.
/// </summary>
public class ImageUsageResolverTests
{
    private static ImageInfo Image(string id, string repository = "<none>", string tag = "<none>") =>
        new() { Id = id, Repository = repository, Tag = tag, Size = 1024 };

    private static ContainerInfo Container(string name, string image) =>
        new() { Id = "c" + name, Name = name, Image = image };

    /// <summary>
    /// The real case: pulling a newer ollama/ollama:latest untagged the previous image, which the
    /// running container still holds. `list` reports the container's image as a short ID while the
    /// image itself carries the full one.
    /// </summary>
    [Fact]
    public void AnUntaggedImageStillHeldByAContainerIsReportedAsInUse()
    {
        var dangling = Image("2a5d04622211ab0f6bd6a1bb0bd8b70a1a5bd8b8ba4f41d5d2e9f3c9d1a0b7c6");
        var images = new[] { dangling, Image("31aae755296d0000000000000000000000000000000000000000000000000000", "ollama/ollama", "latest") };

        ImageUsageResolver.Apply(images, [Container("wslcd-ollama", "2a5d04622211")]);

        Assert.True(dangling.IsInUse);
        Assert.Equal("wslcd-ollama", dangling.UsedBy);
        Assert.Equal("In use by wslcd-ollama", dangling.UsedByCaption);
        // The newly tagged image is not the one the container holds.
        Assert.False(images[1].IsInUse);
    }

    [Fact]
    public void AContainerReferencingAnImageByNameMarksIt()
    {
        var mysql = Image("7c07d11b694d", "mysql", "8");

        ImageUsageResolver.Apply([mysql], [Container("db", "mysql:8")]);

        Assert.Equal("db", mysql.UsedBy);
    }

    /// <summary>A stopped container still pins its image, so it counts exactly like a running one.</summary>
    [Fact]
    public void EveryHoldingContainerIsNamed()
    {
        var image = Image("abcdef012345", "nginx", "alpine");

        ImageUsageResolver.Apply([image], [Container("web", "nginx:alpine"), Container("web-2", "nginx:alpine")]);

        Assert.Equal("web, web-2", image.UsedBy);
    }

    [Fact]
    public void AnImageNothingReferencesIsFree()
    {
        var orphan = Image("ffffffffffff");

        ImageUsageResolver.Apply([orphan], [Container("db", "mysql:8")]);

        Assert.False(orphan.IsInUse);
        Assert.Equal("", orphan.UsedByCaption);
    }

    /// <summary>Re-running the resolver must clear a stamp that no longer applies.</summary>
    [Fact]
    public void UsageIsRecomputedRatherThanAccumulated()
    {
        var image = Image("abcdef012345", "nginx", "alpine");
        ImageUsageResolver.Apply([image], [Container("web", "nginx:alpine")]);
        Assert.True(image.IsInUse);

        ImageUsageResolver.Apply([image], []);

        Assert.False(image.IsInUse);
    }
}
