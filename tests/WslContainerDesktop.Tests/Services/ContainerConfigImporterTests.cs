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

public sealed class ContainerConfigImporterTests
{
    [Fact]
    public void OllamaNamedVolumeSurvivesProfileSerializationEditingAndArguments()
    {
        const string json = """
            [{"Image":"ollama/ollama:latest","Name":"ollama","Mounts":[
              {"Type":"volume","Name":"wslcd-ollama","Source":"wslcd-ollama",
               "Destination":"/root/.ollama","ReadWrite":true}]}]
            """;
        var options = Assert.IsType<RunContainerOptions>(ContainerConfigImporter.FromInspect(json, out var warnings));
        Assert.Empty(warnings);
        Assert.Equal("wslcd-ollama:/root/.ollama", Assert.Single(options.Volumes));

        var loaded = RoundTrip(options);
        var editable = loaded.Options.Clone();
        editable.Name = "ollama-edited";
        Assert.Equal("ollama", loaded.Options.Name);
        Assert.Equal(options.Volumes, editable.Volumes);
        Assert.Equal(options.Volumes, VolumeArguments(editable));
    }

    [Theory]
    [InlineData("C:\\Users\\me\\Model files", "\"ReadOnly\":true", ":ro")]
    [InlineData("\\\\server\\share\\models", "\"RW\":false", ":ro")]
    [InlineData("D:/Model files", "\"ReadWrite\":true", "")]
    public void HostBindPathsAndAccessModesRoundTripWithoutConversion(string source, string mode, string suffix)
    {
        var json = $$"""
            {"Image":"test","Mounts":[{"Type":"bind","Source":{{JsonSerializer.Serialize(source)}},
            "Destination":"/models",{{mode}}}]}
            """;
        var options = Assert.IsType<RunContainerOptions>(ContainerConfigImporter.FromInspect(json, out var warnings));
        Assert.Equal(source + ":/models" + suffix, Assert.Single(options.Volumes));
        var loaded = RoundTrip(options);
        // The edit dialog joins and splits these raw lines; it does not parse away :ro or drive colons.
        loaded.Options.Volumes = string.Join('\n', loaded.Options.Volumes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Assert.Equal(options.Volumes, VolumeArguments(loaded.Options));
        if (suffix == ":ro")
        {
            Assert.Contains(warnings, warning => warning.Contains("read-only", StringComparison.Ordinal));
        }
        else
        {
            Assert.Empty(warnings);
        }
    }

    [Theory]
    [InlineData("\"ReadWrite\":false")]
    [InlineData("\"RW\":false")]
    [InlineData("\"ReadOnly\":true")]
    public void ReadOnlyNamedVolumeEmitsSupportedSuffix(string mode)
    {
        var options = ContainerConfigImporter.FromInspect(
            $$"""{"Image":"test","Mounts":[{"Type":"volume","Name":"data","Destination":"/data",{{mode}}}]}""",
            out var warnings)!;
        Assert.Equal("data:/data:ro", Assert.Single(VolumeArguments(RoundTrip(options).Options)));
        Assert.Contains(warnings, warning => warning.Contains("read-only", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/var/lib/docker/volumes/data/_data")]
    [InlineData("/mnt/c/data")]
    [InlineData("/run/desktop/mnt/host/c/data")]
    [InlineData("relative\\data")]
    [InlineData("C:data")]
    [InlineData("\\\\server")]
    [InlineData("\\\\wsl$\\Ubuntu\\data")]
    [InlineData("\\\\wsl.localhost\\Ubuntu\\data")]
    [InlineData("\\\\?\\C:\\data")]
    [InlineData("\\\\.\\pipe\\name")]
    [InlineData("C:\\data:stream")]
    [InlineData("C:\\data\\..\\other")]
    public void InternalOrAmbiguousBindSourceIsNotFabricated(string source)
    {
        var options = ContainerConfigImporter.FromInspect(
            $$"""{"Image":"test","Mounts":[{"Type":"bind","Source":{{JsonSerializer.Serialize(source)}},"Destination":"/data","RW":true}]}""",
            out var warnings)!;
        Assert.Empty(options.Volumes);
        Assert.Contains(warnings, warning => warning.Contains("No path conversion", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("data", "\"IsAnonymous\":true,")]
    [InlineData("data", "\"Anonymous\":true,")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", "\"IsAnonymous\":false,")]
    public void ExplicitAndLikelyAnonymousVolumesAreExcluded(string name, string metadata)
    {
        var options = ContainerConfigImporter.FromInspect(
            $$"""{"Image":"test","Mounts":[{"Type":"volume","Name":"{{name}}",{{metadata}}"Destination":"/data","RW":true}]}""",
            out var warnings)!;
        Assert.Empty(options.Volumes);
        Assert.Contains(warnings, warning => warning.Contains("anonymous", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"Name\":\"data\",\"Source\":\"/engine/volumes/data\"")]
    [InlineData("\"Source\":\"data\"")]
    public void NamedVolumeUsesIdentifierNotEngineBackingPath(string fields)
    {
        var options = ContainerConfigImporter.FromInspect(
            $$"""{"Image":"test","Mounts":[{"Type":"volume",{{fields}},"Destination":"/data","RW":true}]}""",
            out var warnings)!;
        Assert.Equal("data:/data", Assert.Single(options.Volumes));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("\"Name\":\"one\",\"Source\":\"two\"")]
    [InlineData("\"Source\":\"/engine/volumes/data\"")]
    [InlineData("\"Name\":\"/invalid/name\",\"Source\":\"data\"")]
    public void MissingOrConflictingVolumeIdentityIsOmitted(string fields)
    {
        var options = ContainerConfigImporter.FromInspect(
            $$"""{"Image":"test","Mounts":[{"Type":"volume",{{fields}},"Destination":"/data","RW":true}]}""",
            out var warnings)!;
        Assert.Empty(options.Volumes);
        Assert.NotEmpty(warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"RW\":null")]
    [InlineData(",\"RW\":\"true\"")]
    [InlineData(",\"RW\":true,\"ReadOnly\":true")]
    public void MissingOrConflictingAccessModeIsNeverAssumedWritable(string fields)
    {
        var options = ContainerConfigImporter.FromInspect(
            $$"""{"Image":"test","Mounts":[{"Type":"volume","Name":"data","Destination":"/data"{{fields}}}]}""",
            out var warnings)!;
        Assert.Empty(options.Volumes);
        Assert.Contains(warnings, warning => warning.Contains("not assumed writable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/data", "data", true, 1)]
    [InlineData("/data/", "data", true, 1)]
    [InlineData("//data", "data", true, 1)]
    [InlineData("/data", "other", true, 0)]
    [InlineData("/data", "data", false, 0)]
    public void DuplicateTargetsAreDeduplicatedOrOmittedAsAGroup(string target, string name, bool writable, int count)
    {
        var json = $$"""
            {"Image":"test","Mounts":[
              {"Type":"volume","Name":"data","Destination":"/data","RW":true},
              {"Type":"volume","Name":"{{name}}","Destination":"{{target}}","RW":{{(writable ? "true" : "false")}}}]}
            """;
        var options = ContainerConfigImporter.FromInspect(json, out var warnings)!;
        Assert.Equal(count, options.Volumes.Count);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void UnsupportedDuplicateBlocksOtherwiseValidTargetButNotOtherMounts()
    {
        var options = ContainerConfigImporter.FromInspect("""
            {"Image":"test","Mounts":[
              {"Type":"volume","Name":"data","Destination":"/data","RW":true},
              {"Type":"tmpfs","Destination":"/data","RW":true},
              {"Type":"volume","Name":"keep","Destination":"/keep","RW":true}]}
            """, out var warnings)!;
        Assert.Equal("keep:/keep", Assert.Single(options.Volumes));
        Assert.Contains(warnings, warning => warning.Contains("duplicate destinations", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("unsupported", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"Mounts\":null")]
    [InlineData(",\"Mounts\":{}")]
    [InlineData(",\"Mounts\":[null]")]
    public void LegacyOrMalformedMountMetadataWarnsWithoutPreventingProfileCreation(string mounts)
    {
        var options = ContainerConfigImporter.FromInspect($$"""{"Image":"test"{{mounts}}}""", out var warnings)!;
        Assert.Equal("test", options.Image);
        Assert.Empty(options.Volumes);
        Assert.NotEmpty(warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("/data:ro")]
    [InlineData("/data/../other")]
    [InlineData("/data ")]
    public void UnsafeDestinationsAreNotEmitted(string target)
    {
        var options = ContainerConfigImporter.FromInspect(
            $$"""{"Image":"test","Mounts":[{"Type":"volume","Name":"data","Destination":"{{target}}","RW":true}]}""",
            out var warnings)!;
        Assert.Empty(options.Volumes);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void ExplicitEmptyMountArrayHasNoStorageWarningAndImageFilteringIsUnchanged()
    {
        const string json = """
            {"Image":"id","Config":{"Image":"test:latest","Env":["BAKED=1","CUSTOM=2"],
              "Cmd":["serve"],"Entrypoint":["entry"],"WorkingDir":"/app","User":"app"},
              "Mounts":[],"NetworkSettings":null}
            """;
        const string image = """
            {"Config":{"Env":["BAKED=1"],"Cmd":["serve"],"Entrypoint":["entry"],
              "WorkingDir":"/app","User":"app"}}
            """;
        var options = ContainerConfigImporter.FromInspect(json, out var warnings, image)!;
        Assert.Empty(warnings);
        Assert.Empty(options.Volumes);
        Assert.Equal("test:latest", options.Image);
        Assert.Equal("CUSTOM=2", Assert.Single(options.EnvironmentVariables));
        Assert.Null(options.Command);
        Assert.Null(options.Entrypoint);
        Assert.Null(options.WorkingDir);
        Assert.Null(options.User);
        Assert.Equal(options.Image, ContainerConfigImporter.FromInspect(json, image)!.Image);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[false]")]
    [InlineData("42")]
    public void MalformedInspectRootReturnsNullInsteadOfThrowing(string json)
    {
        Assert.Null(ContainerConfigImporter.FromInspect(json, out var warnings));
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void ExistingProfileJsonWithoutMountsRemainsBackwardReadable()
    {
        var profiles = JsonSerializer.Deserialize<List<RunProfile>>("""
            [{"Name":"legacy","Options":{"Image":"test","PortMappings":["80:80"]}}]
            """)!;
        var profile = Assert.Single(profiles);
        Assert.Empty(profile.Options.Volumes);
        Assert.Equal("80:80", Assert.Single(profile.Options.PortMappings));
    }

    private static RunProfile RoundTrip(RunContainerOptions options)
    {
        // Match RunProfileStore's list schema without reading or overwriting the user's real store.
        var json = JsonSerializer.Serialize(new[] { new RunProfile { Name = "saved", Options = options } },
            new JsonSerializerOptions { WriteIndented = true });
        return Assert.Single(JsonSerializer.Deserialize<List<RunProfile>>(json)!);
    }

    private static List<string> VolumeArguments(RunContainerOptions options)
    {
        var args = options.ToArguments();
        return args.Select((arg, index) => (arg, index))
            .Where(item => item.arg == "-v").Select(item => args[item.index + 1]).ToList();
    }
}
