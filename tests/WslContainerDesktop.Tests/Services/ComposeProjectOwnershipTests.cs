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

public sealed class ComposeProjectOwnershipTests
{
    [Fact]
    public void TryReplaceRejectsChangedInstanceAndPreservesCurrentProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "wcd-compose-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, root);
            var original = new ComposeProject { Name = "probe", SessionOwner = AutomationSessionOwner.Capture("engine", new("alpha")) };
            var current = new ComposeProject { Name = "probe", SessionOwner = AutomationSessionOwner.Capture("engine", new("beta")) };
            store.Save(original);
            store.Save(current);
            Assert.False(store.TryReplace(original, new ComposeProject { Name = "probe" }));
            Assert.Same(current, store.Get("probe"));

            var updated = new ComposeProject { Name = "PROBE", ImportedOnly = true };
            Assert.True(store.TryReplace(current, updated));
            Assert.Equal("beta", updated.SessionOwner!.SessionName);
            var restored = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, root).Get("probe")!;
            Assert.True(restored.ImportedOnly);
            Assert.Equal("beta", restored.SessionOwner!.SessionName);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TryAddNewDoesNotReplaceCaseInsensitiveExistingProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "wcd-compose-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, root);
            var original = new ComposeProject { Name = "Probe", SessionOwner = AutomationSessionOwner.Capture("engine", new("alpha")) };
            Assert.True(store.TryAddNew(original));
            Assert.False(store.TryAddNew(new ComposeProject
            {
                Name = " probe ", SessionOwner = AutomationSessionOwner.Capture("engine", new("beta")),
            }));
            Assert.Same(original, store.Get("probe"));
            Assert.Equal("alpha", new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, root)
                .Get("probe")!.SessionOwner!.SessionName);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TryDeleteDoesNotRemoveAReplacementWithTheSameName()
    {
        var root = Path.Combine(Path.GetTempPath(), "wcd-compose-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, root);
            var original = new ComposeProject { Name = "probe", SessionOwner = AutomationSessionOwner.Capture("engine", new("alpha")) };
            var replacement = new ComposeProject { Name = "probe", SessionOwner = AutomationSessionOwner.Capture("engine", new("beta")) };
            store.Save(original);
            store.Save(replacement);
            Assert.False(store.TryDelete(original));
            Assert.Same(replacement, store.Get("probe"));
            Assert.Equal("beta", new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, root)
                .Get("probe")!.SessionOwner!.SessionName);
            Assert.True(store.TryDelete(replacement));
            Assert.Null(store.Get("probe"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LegacyProjectStaysUnassignedUntilOwnerIsSaved()
    {
        var root = Path.Combine(Path.GetTempPath(), "wcd-compose-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "compose-projects.json"), """[{"Name":"legacy","Services":[]}]""");
            var store = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, root);
            Assert.Null(store.Get("legacy")!.SessionOwner);
            store.Save(new ComposeProject
            {
                Name = "legacy",
                SessionOwner = AutomationSessionOwner.Capture("engine", new("SDK")),
            });

            var restored = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, root);
            Assert.Equal("SDK", restored.Get("legacy")!.SessionOwner!.SessionName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedSaveDoesNotPublishAnInMemoryAssignment()
    {
        var root = Path.Combine(Path.GetTempPath(), "wcd-compose-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var blocker = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blocker, "keep");
            var store = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, blocker);
            var changed = 0;
            store.Changed += (_, _) => changed++;

            Assert.Throws<InvalidOperationException>(() => store.Save(new ComposeProject
            {
                Name = "legacy", SessionOwner = AutomationSessionOwner.Capture("engine", WslcSessionTarget.Default),
            }));

            Assert.Empty(store.GetAll());
            Assert.Equal(0, changed);
            Assert.Equal("keep", File.ReadAllText(blocker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
