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

public sealed class ManagedSessionStoreTests
{
    [Fact]
    public void PendingRemovalPersistsItsExactOwnerAcrossRestart()
    {
        using var root = new TestRoot();
        var store = new ManagedSessionStore(NullLogger.Instance, root.Path);
        var definition = new ManagedSessionDefinition(Guid.NewGuid(), "work");
        store.Add(definition);
        definition = store.MarkEngine(definition, @"C:\wslc.exe");
        var owner = AutomationSessionOwner.Capture(@"C:\wslc.exe", new("work"));
        store.MarkForDeletion(definition, owner);
        var saved = Assert.Single(new ManagedSessionStore(NullLogger.Instance, root.Path).Snapshot());
        Assert.True(saved.PendingDeletion);
        Assert.Equal(owner, saved.PendingRemovalOwner);
        Assert.Equal(@"C:\wslc.exe", saved.LastExecutablePath);
    }

    [Fact]
    public void DefinitionsAndPendingDeletionSurviveRestart()
    {
        using var root = new TestRoot();
        var store = new ManagedSessionStore(NullLogger.Instance, root.Path);
        var definition = new ManagedSessionDefinition(Guid.NewGuid(), "Example");
        store.Add(definition);
        var pending = store.MarkForDeletion(definition);

        var reloaded = new ManagedSessionStore(NullLogger.Instance, root.Path);
        Assert.Equal(pending, Assert.Single(reloaded.Snapshot()));
        Assert.True(pending.PendingDeletion);
        reloaded.Remove(pending);
        Assert.Empty(new ManagedSessionStore(NullLogger.Instance, root.Path).Snapshot());
    }

    [Fact]
    public void StorageLocationUsesOnlyTheGeneratedIdentity()
    {
        using var root = new TestRoot();
        var store = new ManagedSessionStore(NullLogger.Instance, root.Path);
        var id = Guid.NewGuid();
        var definition = new ManagedSessionDefinition(id, "..\\not-a-path");
        Assert.Equal(Path.Combine(root.Path, "storage", id.ToString("N")), store.StoragePath(definition));
    }

    [Fact]
    public void CorruptSavedSessionsArePreservedAndCannotBeOverwritten()
    {
        using var root = new TestRoot();
        var file = Path.Combine(root.Path, "sessions.json");
        File.WriteAllText(file, "{broken");
        var store = new ManagedSessionStore(NullLogger.Instance, root.Path);

        Assert.True(store.HasLoadError);
        Assert.Throws<InvalidOperationException>(() => store.Add(new(Guid.NewGuid(), "new")));
        Assert.Equal("{broken", File.ReadAllText(file));
    }

    [Fact]
    public void FailedSaveDoesNotPublishNewDefinition()
    {
        using var root = new TestRoot();
        var blocker = Path.Combine(root.Path, "file");
        File.WriteAllText(blocker, "not a directory");
        var store = new ManagedSessionStore(NullLogger.Instance, Path.Combine(blocker, "sessions"));

        Assert.ThrowsAny<IOException>(() => store.Add(new(Guid.NewGuid(), "new")));
        Assert.Empty(store.Snapshot());
    }

    private sealed class TestRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wcd-session-store-" + Guid.NewGuid().ToString("N"));
        public TestRoot() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
