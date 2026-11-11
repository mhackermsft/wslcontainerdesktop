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
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class SessionInventoryGuardTests
{
    [Fact]
    public void RequiresObservedSelectionAndCurrentRowInstances()
    {
        var context = new WslcSessionContext();
        context.Select("first");
        var guard = new SessionInventoryGuard<object>(context);
        var row = new object();
        var replacement = new object();
        guard.Observe(context.CaptureSnapshot());
        Assert.True(guard.AreCurrent([row], [row]));
        Assert.False(guard.AreCurrent([replacement], [row]));

        context.Select("second");
        context.Select("first");
        Assert.False(guard.AreCurrent([row], [row]));
        guard.Observe(context.CaptureSnapshot());
        Assert.True(context.TryApplyIfUnlocked(() => { }));
        Assert.False(guard.AreCurrent([row], [row]));
        guard.Observe(context.CaptureSnapshot());
        guard.Invalidate();
        Assert.False(guard.AreCurrent([row], [row]));
    }
}
