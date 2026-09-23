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

public sealed class ImagePullProgressTests
{
    // Captured verbatim from `wslc pull alpine:3.20` with output redirected (wslc 2.9.12.0).
    private static readonly string[] RealAlpinePull =
    [
        "3.20: Pulling from library/alpine",
        "25f1d6b1951a: Pulling fs layer",
        "25f1d6b1951a: Downloading",
        "25f1d6b1951a: Verifying Checksum",
        "25f1d6b1951a: Download complete",
        "25f1d6b1951a: Extracting",
        "25f1d6b1951a: Pull complete",
        "Digest: sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc",
        "Status: Downloaded newer image for alpine:3.20",
        "docker.io/library/alpine:3.20",
    ];

    [Fact]
    public void ReportsOnlyChangesForARealPull()
    {
        var progress = new ImagePullProgress("the image");

        var messages = RealAlpinePull.Select(progress.Observe).OfType<string>().ToList();

        Assert.Equal(
        [
            "Downloading the image: 0 of 1 layers downloaded...",
            "Unpacking the image: 0 of 1 layers ready...",
            "Unpacking the image: 1 of 1 layers ready...",
        ], messages);
    }

    [Fact]
    public void CountsLayersAcrossInterleavedStatus()
    {
        var progress = new ImagePullProgress("the image");
        progress.Observe("aaaaaaaaaaaa: Pulling fs layer");
        progress.Observe("bbbbbbbbbbbb: Waiting");
        progress.Observe("cccccccccccc: Already exists");

        Assert.Equal("Downloading the image: 2 of 3 layers downloaded...", progress.Observe("aaaaaaaaaaaa: Download complete"));
        Assert.Equal("Unpacking the image: 1 of 3 layers ready...", progress.Observe("bbbbbbbbbbbb: Download complete"));
    }

    [Fact]
    public void NeverMovesALayerBackwards()
    {
        var progress = new ImagePullProgress("the image");
        progress.Observe("aaaaaaaaaaaa: Pull complete");

        Assert.Null(progress.Observe("aaaaaaaaaaaa: Downloading"));
    }

    [Theory]
    [InlineData("latest: Pulling from ollama/ollama")]
    [InlineData("Digest: sha256:0123456789abcdef")]
    [InlineData("Status: Image is up to date for ollama/ollama:latest")]
    [InlineData("")]
    [InlineData("not-hex-id!: Pulling fs layer")]
    public void IgnoresNonLayerLines(string line) =>
        Assert.Null(new ImagePullProgress("the image").Observe(line));
}
