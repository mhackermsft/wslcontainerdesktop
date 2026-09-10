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

using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class FoundryLocalRecordedCliTests
{
    private static Dictionary<string, CommandResult> ReadCapture(string file = "help.json")
    {
        using var capture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Foundry", "0.10.3", file)));
        Assert.Equal("Microsoft.FoundryLocal_0.10.3.0_x64__8wekyb3d8bbwe",
            capture.RootElement.GetProperty("PackageFullName").GetString());
        Assert.Equal("86A01C52265BD9C9167C1F8A04F34621A2A63EC5C8276166C2EECC8C6A56553F",
            capture.RootElement.GetProperty("PackageSha256").GetString());
        return capture.RootElement.GetProperty("Commands").EnumerateArray().ToDictionary(
            c => c.GetProperty("Arguments").GetString()!,
            c => new CommandResult
            {
                ExitCode = c.GetProperty("ExitCode").GetInt32(),
                StandardOutput = c.GetProperty("Stdout").GetString()!,
                StandardError = c.GetProperty("Stderr").GetString()!,
            }, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Recorded0103HelpSelectsServerStatusButDoesNotPretendStatusWasCaptured()
    {
        var capture = ReadCapture();
        var calls = new List<string>();
        var cli = new FoundryLocalCli(() => @"C:\Fixture\foundry.exe", (start, _) =>
        {
            var command = string.Join(" ", start.ArgumentList);
            calls.Add(command);
            if (capture.TryGetValue(command, out var result)) return Task.FromResult(result);
            Assert.Equal("server status", command);
            // Actual status was not executed in the help-only capture. An unknown
            // response must stay unknown rather than inventing a live endpoint.
            return Task.FromResult(new CommandResult { StandardOutput = "Unrecorded status schema" });
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => cli.DiscoverAsync(null, default));
        Assert.Equal(["--version", "--help", "server --help", "server status"], calls);
    }

    [Fact]
    public void RecordedModelHelpDoesNotProvePinnedVersionDownloadOrOfflineLoad()
    {
        var capture = ReadCapture();
        Assert.Equal("0.10.3", capture["--version"].StandardOutput.Trim());
        Assert.True(FoundryLocalCli.AdvertisesCommand(capture["model --help"].StandardOutput, "download"));
        Assert.True(FoundryLocalCli.AdvertisesCommand(capture["model --help"].StandardOutput, "load"));
        Assert.False(FoundryLocalCli.AdvertisesCommand(capture["model --help"].StandardOutput, "import"));
        Assert.Contains("Model alias, variant id, or model id", capture["model download --help"].StandardOutput);
        Assert.DoesNotContain("--offline", capture["model load --help"].StandardOutput);
        Assert.DoesNotContain("--version", capture["model download --help"].StandardOutput);
        Assert.All(capture.Values, result => { Assert.True(result.Success); Assert.Empty(result.StandardError); });
    }

    [Fact]
    public void RecordedLifecycleHelpEstablishesRandomPortButNotOfflineOrImport()
    {
        var capture = ReadCapture("lifecycle-help.json");
        Assert.True(FoundryLocalCli.AdvertisesCommand(capture["cache --help"].StandardOutput, "list"));
        Assert.True(FoundryLocalCli.AdvertisesCommand(capture["cache --help"].StandardOutput, "location"));
        Assert.False(FoundryLocalCli.AdvertisesCommand(capture["cache --help"].StandardOutput, "import"));
        Assert.Contains("Use 0 for an OS-assigned port", capture["server start --help"].StandardOutput);
        Assert.Contains("--idle-timeout <minutes>", capture["server start --help"].StandardOutput);
        Assert.DoesNotContain("--offline", capture["server start --help"].StandardOutput);
        Assert.Contains("Persist a configuration value", capture["config set --help"].StandardOutput);
        Assert.Contains("restart confirmation", capture["config set --help"].StandardOutput);
        Assert.All(capture.Values, result => { Assert.True(result.Success); Assert.Empty(result.StandardError); });
    }

    [Theory]
    [InlineData("Description:\n  server is documented elsewhere\nOptions:\n  --help")]
    [InlineData("Commands:\n  model Models\nExamples:\n  server status")]
    [InlineData("Commands:\n  serverless Something else")]
    public void ProseExamplesAndPrefixMatchesCannotAdvertiseCommands(string text) =>
        Assert.False(FoundryLocalCli.AdvertisesCommand(text, "server"));
}
