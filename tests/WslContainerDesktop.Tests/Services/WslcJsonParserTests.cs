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

using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class WslcJsonParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    public void RuntimeInventories_DistinguishSuccessfulEmptyFromFailedCommand(string output)
    {
        Assert.Empty(WslcJsonParser.ParseVolumes(new() { StandardOutput = output }));
        Assert.Empty(WslcJsonParser.ParseImages(new() { StandardOutput = output }));
        var failed = new CommandResult { ExitCode = 1, StandardOutput = output, StandardError = "synthetic secret" };
        Assert.DoesNotContain("synthetic secret", Assert.Throws<InvalidOperationException>(
            () => WslcJsonParser.ParseVolumes(failed)).Message);
        Assert.Throws<InvalidOperationException>(() => WslcJsonParser.ParseImages(failed));
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("""[{"Name":"same"},{"Name":"SAME"}]""")]
    [InlineData("""{"Name":"valid"} invalid""")]
    public void VolumeInventory_RejectsPartialOrMissingIdentity(string output) =>
        Assert.ThrowsAny<JsonException>(() => WslcJsonParser.ParseVolumes(new() { StandardOutput = output }));

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("""{"Id":"valid"} invalid""")]
    public void ImageInventory_RejectsPartialOrMissingIdentity(string output) =>
        Assert.ThrowsAny<JsonException>(() => WslcJsonParser.ParseImages(new() { StandardOutput = output }));

    [Fact]
    public void RuntimeInventories_PreserveLegacyObjectStreamsAndSharedImageTags()
    {
        Assert.Equal(2, WslcJsonParser.ParseVolumes(new() { StandardOutput = """{"Name":"one"} {"Name":"two"}""" }).Count);
        Assert.Equal(2, WslcJsonParser.ParseImages(new() { StandardOutput = """{"Id":"same","Tag":"one"} {"Id":"same","Tag":"two"}""" }).Count);
    }

    [Fact]
    public void ParseList_LegacyArray_ReturnsAllObjects()
    {
        const string output =
            """
            [
              { "Id": "sha256:first", "Repository": "example/first", "Tag": "latest", "Created": 1, "Size": 10 },
              { "Id": "sha256:second", "Repository": "example/second", "Tag": "dev", "Created": 2, "Size": 20 }
            ]
            """;

        var images = WslcJsonParser.ParseList<TestImage>(output);

        Assert.Equal(["sha256:first", "sha256:second"], images.Select(image => image.Id));
    }

    [Fact]
    public void ParseList_NewlineDelimitedObjects_ReturnsAllObjects()
    {
        const string output =
            """
            {"Id":"sha256:first","Repository":"example/first","Tag":"latest","Created":1,"Size":10}
            {"Id":"sha256:second","Repository":"example/second","Tag":"dev","Created":2,"Size":20}
            """;

        var images = WslcJsonParser.ParseList<TestImage>(output);

        Assert.Equal(["sha256:first", "sha256:second"], images.Select(image => image.Id));
    }

    [Fact]
    public void ParseList_SingleObject_ReturnsOneObject()
    {
        const string output =
            """{"Id":"sha256:single","Repository":"example/single","Tag":"latest","Created":1,"Size":10}""";

        var image = Assert.Single(WslcJsonParser.ParseList<TestImage>(output));

        Assert.Equal("sha256:single", image.Id);
    }

    [Fact]
    public void ParseList_LeadingBom_ParsesObject()
    {
        const string output =
            "\uFEFF{\"Id\":\"sha256:bom\",\"Repository\":\"example/bom\",\"Tag\":\"latest\",\"Created\":1,\"Size\":10}";

        var image = Assert.Single(WslcJsonParser.ParseList<TestImage>(output));

        Assert.Equal("sha256:bom", image.Id);
    }

    [Fact]
    public void ParseList_EmptyOutput_ReturnsEmptyList()
    {
        var images = WslcJsonParser.ParseList<TestImage>(" \r\n\t");

        Assert.Empty(images);
    }

    [Theory]
    [InlineData("{\"Id\":\"sha256:first\"}\n{\"Id\":")]
    [InlineData("{\"Id\":\"sha256:first\"}\n[{\"Id\":\"sha256:second\"}]")]
    public void ParseList_MalformedOrMixedOutput_ThrowsJsonException(string output)
    {
        Assert.Throws<JsonException>(() => WslcJsonParser.ParseList<TestImage>(output));
    }

    private sealed class TestImage
    {
        public string Id { get; set; } = string.Empty;
    }
}
