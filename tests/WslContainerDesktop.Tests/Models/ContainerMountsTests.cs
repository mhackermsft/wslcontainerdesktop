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
using Xunit;

namespace WslContainerDesktop.Tests.Models;

public sealed class ContainerMountsTests
{
    [Fact]
    public void ObservedOllamaMountIsSharedWithDetails()
    {
        const string json = """
            [{"Mounts":[{"Type":"volume","Name":"wslcd-ollama","Source":"wslcd-ollama",
            "Destination":"/root/.ollama","ReadWrite":true}]}]
            """;
        var result = ContainerMounts.Parse(json);
        Assert.True(result.IsComplete);
        var mount = Assert.Single(result.Items);
        Assert.Equal("wslcd-ollama", mount.VolumeName);
        Assert.False(mount.ReadOnly);
        Assert.Equal("wslcd-ollama -> /root/.ollama", Assert.Single(ContainerDetails.Parse(json).Mounts));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{"Mounts":null}""")]
    [InlineData("""{"Mounts":[null]}""")]
    [InlineData("""{"Mounts":[{"Type":42,"Destination":"/data"}]}""")]
    public void LegacyOrMalformedMetadataIsUnknown(string json)
    {
        var result = ContainerMounts.Parse(json);
        Assert.False(result.IsComplete);
        Assert.NotEmpty(result.Warnings);
    }

    [Theory]
    [InlineData("bind", "data", "C:\\data", null)]
    [InlineData("volume", null, "/var/lib/volumes/data", null)]
    [InlineData("volume", null, "data", "data")]
    [InlineData("volume", "data", "/var/lib/volumes/data", "data")]
    [InlineData("tmpfs", "data", "data", null)]
    public void OnlyVolumeIdentifiersMapToVolumes(string type, string? name, string source, string? expected)
    {
        Assert.Equal(expected, new ContainerMount(type, name, source, "/data", false, null).VolumeName);
    }

    [Theory]
    [InlineData("\"RW\":false", true)]
    [InlineData("\"ReadWrite\":true", false)]
    [InlineData("\"ReadOnly\":true", true)]
    [InlineData("\"ReadWrite\":false,\"RW\":false", true)]
    [InlineData("\"ReadWrite\":false,\"RW\":true", null)]
    [InlineData("\"ReadOnly\":\"true\"", null)]
    [InlineData("\"Mode\":\"ro\"", null)]
    public void AccessModeNeverDefaultsUnknownToWritable(string fields, bool? expected)
    {
        var json = $$"""{"Mounts":[{"Type":"volume","Name":"data","Destination":"/data",{{fields}}}]}""";
        Assert.Equal(expected, Assert.Single(ContainerMounts.Parse(json).Items).ReadOnly);
    }

    [Fact]
    public void EmptyArrayIsAnExplicitCompleteMountList()
    {
        var result = ContainerMounts.Parse("""{"Mounts":[]}""");
        Assert.True(result.IsComplete);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void MalformedSiblingDoesNotDiscardValidMounts()
    {
        var result = ContainerMounts.Parse("""
            {"Mounts":[{},{"Type":"volume","Name":"data","Destination":"/data"},false]}
            """);
        Assert.False(result.IsComplete);
        Assert.Contains(result.Items, m => m.VolumeName == "data");
    }
}
