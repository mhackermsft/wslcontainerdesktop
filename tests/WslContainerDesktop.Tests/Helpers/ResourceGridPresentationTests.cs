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

using System.Xml.Linq;
using Xunit;

namespace WslContainerDesktop.Tests.Helpers;

public sealed class ResourceGridPresentationTests
{
    [Theory]
    [InlineData("ContainersPage")]
    [InlineData("ImagesPage")]
    [InlineData("VolumesPage")]
    [InlineData("NetworksPage")]
    [InlineData("EndpointsPage")]
    public void FilterControlSharesActionRowAndNoMatchesRemainVisible(string page)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WslContainerDesktop.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "src", "WslContainerDesktop", "Views", page + ".xaml");
        var document = XDocument.Load(path);
        XNamespace controls = "using:WslContainerDesktop.Views.Controls";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var toolbar = Assert.Single(document.Descendants(controls + "ResourceGridToolbar"));
        Assert.Single(toolbar.Ancestors(), element =>
            (string?)element.Attribute("Grid.Row") == "1");
        Assert.DoesNotContain(toolbar.AncestorsAndSelf(), element =>
            ((string?)element.Attribute("Visibility"))?.Contains("IsSelectionMode", StringComparison.Ordinal) == true);
        var table = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(xaml + "Name") == "TableRoot");
        Assert.Equal("2", (string?)table.Attribute("Grid.Row"));
        var noMatches = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(xaml + "Uid") == "ResourceGrid_NoMatches");
        Assert.Contains("Grid.HasNoMatches", (string?)noMatches.Attribute("Visibility"));
    }
}
