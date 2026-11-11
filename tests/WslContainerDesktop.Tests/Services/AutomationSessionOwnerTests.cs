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

using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AutomationSessionOwnerTests
{
    [Fact]
    public void OwnershipMatchesExecutableAndCaseSensitiveSession()
    {
        var owner = AutomationSessionOwner.Capture(@"C:\WSL\wslc.exe", new WslcSessionTarget("first"));

        Assert.True(owner.Matches(@"c:\wsl\WSLC.exe", new WslcSessionTarget("first")));
        Assert.False(owner.Matches(@"c:\wsl\WSLC.exe", new WslcSessionTarget("FIRST")));
        Assert.False(owner.Matches(@"c:\wsl\WSLC.exe", WslcSessionTarget.Default));
        Assert.False(owner.Matches(@"c:\other\wslc.exe", new WslcSessionTarget("first")));
        Assert.False(new AutomationSessionOwner().Matches("", WslcSessionTarget.Default));
    }

    [Fact]
    public void LegacyPoliciesRemainUnassignedAndExplicitOwnersSurviveReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wcd-owner-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "settings.json"), """
                {"HealthChecks":[{"ContainerName":"web","Kind":0,"Command":"probe","Enabled":true}],
                 "RestartPolicies":[{"ContainerName":"db","Policy":1,"Enabled":true}]}
                """);
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            settings.Load();
            var health = Assert.Single(settings.HealthChecks);
            var restart = Assert.Single(settings.RestartPolicies);
            Assert.Null(health.SessionOwner);
            Assert.Null(restart.SessionOwner);

            health.SessionOwner = AutomationSessionOwner.Capture(@"C:\WSL\wslc.exe", new WslcSessionTarget("SDK"));
            restart.SessionOwner = AutomationSessionOwner.Capture(@"C:\WSL\wslc.exe", WslcSessionTarget.Default);
            settings.Save();
            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            reloaded.Load();

            Assert.Equal(health.SessionOwner, Assert.Single(reloaded.HealthChecks).SessionOwner);
            Assert.Equal(restart.SessionOwner, Assert.Single(reloaded.RestartPolicies).SessionOwner);
            Assert.Equal(health.SessionOwner, health.Clone().SessionOwner);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

    }

    [Fact]
    public void FailedSettingsSavePreservesPreviousFileAndReportsUntilSuccessfulRetry()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var directory = Path.Combine(Path.GetTempPath(), "wcd-owner-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            settings.HealthChecks.Add(new() { ContainerName = "web", Command = "probe" });
            settings.Save();
            var file = Path.Combine(directory, "settings.json");
            var original = File.ReadAllText(file);
            settings.HealthChecks[0].Command = "new-probe";
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                settings.Save();
                Assert.True(settings.HasSaveError);
                Assert.Equal(original, File.ReadAllText(file));
            }

            settings.Save();
            Assert.False(settings.HasSaveError);
            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            reloaded.Load();
            Assert.Equal("new-probe", Assert.Single(reloaded.HealthChecks).Command);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"HealthChecks\":[{\"ContainerName\":\"web\",\"Command\":\"\",\"Enabled\":true}]}")]
    [InlineData("{\"HealthChecks\":[{\"ContainerName\":\"web\",\"Kind\":99,\"Enabled\":true}]}")]
    [InlineData("{\"RestartPolicies\":[{\"ContainerName\":\"web\",\"Policy\":99,\"Enabled\":true}]}")]
    public void UnreadableSettingsArePreservedUntilRepaired(string original)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wcd-owner-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "settings.json");
            File.WriteAllText(file, original);
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            settings.Load();
            Assert.True(settings.HasLoadError);

            settings.Save();
            Assert.True(settings.HasSaveError);
            Assert.Equal(original, File.ReadAllText(file));

            File.WriteAllText(file, "{}");
            settings.Load();
            Assert.False(settings.HasLoadError);
            settings.Save();
            Assert.False(settings.HasSaveError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
