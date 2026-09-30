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

/// <summary>
/// Compose keeps each service volume both as a <c>-v</c> string and as a structured <c>--mount</c>.
/// These tests make sure a container path is never mounted twice once the <c>-v</c> copy is
/// rewritten, which WSLC rejects with "Duplicate mount point" (seen with the WordPress stack).
/// </summary>
public sealed class RunContainerVolumeDedupTests
{
    private static List<string> MountTargets(IReadOnlyList<string> args)
    {
        var targets = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-v")
                targets.Add(RunContainerOptions.VolumeSpecTarget(args[i + 1]));
            else if (args[i] == "--mount")
                targets.Add(args[i + 1].Split(',').Single(p => p.StartsWith("target=", StringComparison.Ordinal))["target=".Length..]);
        }

        return targets;
    }

    [Fact]
    public void ProjectPrefixedNamedVolume_IsMountedOnce()
    {
        var project = ComposeImporter.ParseProject("""
            services:
              db:
                image: mysql:8
                volumes:
                  - db_data:/var/lib/mysql
            volumes:
              db_data:
            """);
        project.Name = "wordpress";
        project.ApplyProjectNamespacing();

        var args = Assert.Single(project.Services).Options.ToArguments();

        Assert.Equal(["/var/lib/mysql"], MountTargets(args));
        Assert.Contains("wordpress_db_data:/var/lib/mysql", args);
    }

    [Fact]
    public void UnchangedVolume_IsStillEmittedAsNativeMount()
    {
        var options = new RunContainerOptions { Image = "img" };
        var mount = new RunContainerMount { Type = "volume", Source = "data", Target = "/data" };
        options.Mounts.Add(mount);
        options.Volumes.Add(mount.ToVolumeSpec());

        var args = options.ToArguments();

        Assert.Equal(["/data"], MountTargets(args));
        Assert.Contains("--mount", args);
        Assert.DoesNotContain("-v", args);
    }

    [Fact]
    public void AnonymousVolumeReplacedByPreservedNamedVolume_IsMountedOnce()
    {
        var options = new RunContainerOptions { Image = "img" };
        options.Mounts.Add(new RunContainerMount { Type = "volume", Target = "/data" });
        options.Volumes.Add("0123abcd:/data");

        Assert.Equal(["/data"], MountTargets(options.ToArguments()));
    }

    [Fact]
    public void BindMountsAreNeverDroppedForAVolumeAtAnotherPath()
    {
        var options = new RunContainerOptions { Image = "img" };
        options.Mounts.Add(new RunContainerMount { Type = "bind", Source = "/host/src", Target = "/src" });
        options.Volumes.Add("cache:/cache");

        Assert.Equal(["/cache", "/src"], MountTargets(options.ToArguments()).Order().ToList());
    }

    [Theory]
    [InlineData("data:/var/lib/data", "/var/lib/data")]
    [InlineData("data:/var/lib/data:ro", "/var/lib/data")]
    [InlineData("/anonymous", "/anonymous")]
    [InlineData("C:/src/app:/app", "/app")]
    public void VolumeSpecTargetReadsTheContainerPath(string spec, string target) =>
        Assert.Equal(target, RunContainerOptions.VolumeSpecTarget(spec));
}
