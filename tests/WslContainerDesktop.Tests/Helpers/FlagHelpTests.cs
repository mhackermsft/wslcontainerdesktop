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

using WslContainerDesktop.Helpers;
using Xunit;

namespace WslContainerDesktop.Tests.Helpers;

/// <summary>Covers the help text attached to container flag metadata so novice-facing descriptions stay present, unique, and shaped like command-line options.</summary>
public sealed class FlagHelpTests
{
    [Fact]
    public void EntriesHaveUserFacingText()
    {
        Assert.NotEmpty(FlagHelp.All);
        foreach (var entry in FlagHelp.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Key));
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.False(string.IsNullOrWhiteSpace(entry.Flag));
            Assert.False(string.IsNullOrWhiteSpace(entry.Text));
            Assert.DoesNotContain('\n', entry.Text);
        }
    }

    [Fact]
    public void EntryKeysAreUnique()
    {
        var duplicates = FlagHelp.All
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void OptionEntriesUseFlagSyntax()
    {
        foreach (var entry in FlagHelp.All.Where(entry => entry.Flag.Contains("--", StringComparison.Ordinal)))
        {
            Assert.StartsWith("-", entry.Flag);
        }
    }
}
