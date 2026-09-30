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
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>
/// Verifies migration of old WSL pre-release channel settings without overriding a user's newer choice.
/// </summary>
public sealed class WslUpdateChannelSettingsTests
{
    [Fact]
    public void PreGaPreReleaseChoiceIsResetOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wsl-channel-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "settings.json"), """{"WslUpdatePreRelease":true}""");
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            settings.Load();
            Assert.False(settings.WslUpdatePreRelease);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void PreReleaseChoiceMadeAfterMigrationIsKept()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wsl-channel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = new SettingsService(NullLogger<SettingsService>.Instance, directory) { WslUpdatePreRelease = true };
            original.Save();
            var restored = new SettingsService(NullLogger<SettingsService>.Instance, directory);
            restored.Load();
            Assert.True(restored.WslUpdatePreRelease);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
