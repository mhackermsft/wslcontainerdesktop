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

public sealed class LegacyPolicyOwnershipStoreTests
{
    [Fact]
    public void AssignmentPersistsAndDoesNotTransferToChangedPolicy()
    {
        using var fixture = new Fixture();
        var health = new HealthCheckConfig { ContainerName = "web", Command = "probe", MaxRestarts = 1 };
        var restart = new RestartPolicyConfig { ContainerName = "web", Policy = RestartPolicyKind.Always };
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        Assert.Null(fixture.Store.OwnerFor(health));
        Assert.Null(fixture.Store.OwnerFor(restart));

        fixture.Store.Assign(health, owner);
        fixture.Store.Assign(restart, AutomationSessionOwner.Capture("engine", WslcSessionTarget.Default));
        var reloaded = fixture.Reload();

        Assert.Equal(owner, reloaded.OwnerFor(health));
        Assert.Null(reloaded.OwnerFor(restart)!.SessionName);
        health.Enabled = false;
        Assert.Equal(owner, reloaded.OwnerFor(health));
        health.Command = "different";
        Assert.Null(reloaded.OwnerFor(health));
    }

    [Fact]
    public void CorruptStoreIsPreservedAndCannotBeOverwritten()
    {
        using var fixture = new Fixture();
        const string corrupt = "{not-json";
        File.WriteAllText(fixture.Path, corrupt);
        var store = fixture.Reload();
        var policy = new HealthCheckConfig { ContainerName = "web", Command = "probe" };

        Assert.True(store.HasLoadError);
        Assert.Null(store.OwnerFor(policy));
        Assert.Throws<InvalidOperationException>(() =>
            store.Assign(policy, AutomationSessionOwner.Capture("engine", WslcSessionTarget.Default)));
        Assert.Equal(corrupt, File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void FailedSaveDoesNotActivateAnAssignment()
    {
        using var fixture = new Fixture();
        var blocker = System.IO.Path.Combine(fixture.Root, "not-a-directory");
        File.WriteAllText(blocker, "keep");
        var store = new LegacyPolicyOwnershipStore(NullLogger<LegacyPolicyOwnershipStore>.Instance,
            System.IO.Path.Combine(blocker, "owners.json"));
        var policy = new HealthCheckConfig { ContainerName = "web", Command = "probe" };

        Assert.ThrowsAny<IOException>(() =>
            store.Assign(policy, AutomationSessionOwner.Capture("engine", WslcSessionTarget.Default)));
        Assert.Null(store.OwnerFor(policy));
        Assert.Equal("keep", File.ReadAllText(blocker));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wcd-owner-store-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "owners.json");
        public LegacyPolicyOwnershipStore Store { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Store = Reload();
        }
        public LegacyPolicyOwnershipStore Reload() => new(NullLogger<LegacyPolicyOwnershipStore>.Instance, Path);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
