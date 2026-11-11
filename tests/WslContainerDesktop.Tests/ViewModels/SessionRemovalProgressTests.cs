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

using WslContainerDesktop.Services;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.ViewModels;

[Collection("UiText")]
public sealed class SessionRemovalProgressTests
{
    [Fact]
    public void DuplicateRemovalIsRejectedAndResultStaysVisible()
    {
        var progress = new SessionRemovalProgress();
        Assert.True(progress.TryBegin("first"));
        Assert.False(progress.TryBegin("second"));
        Assert.True(progress.IsBusy);
        Assert.False(progress.CanEdit);
        Assert.Contains("first", progress.Message);
        progress.Complete();
        Assert.True(progress.CanEdit);
        Assert.True(progress.IsSuccess);
        Assert.Contains("first", progress.Message);
        Assert.True(progress.TryBegin("second"));
        progress.Complete("disk error");
        Assert.True(progress.IsFailure);
        Assert.Contains("disk error", progress.Message);
    }

    [Fact]
    public void MissingSelectionIsSuppressedOnlyIfItAppearedDuringThisRemoval()
    {
        var progress = new SessionRemovalProgress();
        Assert.True(progress.TryBegin("work"));
        Assert.True(progress.SuppressesNewUnavailableWarning("work"));
        Assert.False(progress.SuppressesNewUnavailableWarning("other"));
        progress.Complete("failure");
        Assert.False(progress.SuppressesNewUnavailableWarning("work"));
        Assert.True(progress.TryBegin("work", previouslyUnavailable: "work"));
        Assert.False(progress.SuppressesNewUnavailableWarning("work"));
    }

    [Fact]
    public void LanguageRefreshKeepsTheInProgressOperation()
    {
        UiText.Configure((key, args) => key == "ManagedSession_Removing"
            ? string.Format(UiText.LanguageTag == "zh-Hans" ? "正在移除 {0}" : "Removing {0}", args)
            : key);
        try
        {
            UiText.SetLanguage("en-US");
            var progress = new SessionRemovalProgress();
            Assert.True(progress.TryBegin("work"));
            Assert.Equal("Removing work", progress.Message);
            UiText.SetLanguage("zh-Hans");
            progress.RefreshLanguage();
            Assert.Equal("正在移除 work", progress.Message);
            Assert.True(progress.IsBusy);
            Assert.False(progress.TryBegin("another"));
        }
        finally
        {
            UiText.Configure(null);
            UiText.SetLanguage("en-US");
        }
    }
}
