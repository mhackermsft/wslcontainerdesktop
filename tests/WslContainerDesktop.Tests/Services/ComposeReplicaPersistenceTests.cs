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

using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeReplicaPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory,
        "replica-store-" + Guid.NewGuid().ToString("N"));

    private ComposeProjectStore Open() => new(NullLogger<ComposeProjectStore>.Instance, _directory);

    [Fact]
    public void ReimportPreservesOperatorCountsAndAppliedInstancesAcrossReload()
    {
        var store = Open();
        store.Save(new ComposeProject
        {
            Name = "project",
            Services = [new() { Name = "worker", Replicas = 2 }, new() { Name = "removed" }],
            ReplicaOverrides = new() { ["worker"] = 0, ["removed"] = 3 },
            AppliedServices = new()
            {
                ["worker#2"] = new() { ContainerId = "instance-two", ManuallyStopped = true },
            },
        });
        var imported = ComposeImporter.ParseProject(
            "name: project\nservices: {worker: {image: fixture, scale: 5}}");
        Open().Save(imported);
        var restored = Open().Get("project")!;
        Assert.Equal(5, Assert.Single(restored.Services).Replicas);
        Assert.Equal(0, restored.ReplicaOverrides["worker"]);
        Assert.DoesNotContain("removed", restored.ReplicaOverrides.Keys);
        Assert.Equal("instance-two", restored.AppliedServices["worker#2"].ContainerId);
        Assert.True(restored.AppliedServices["worker#2"].ManuallyStopped);
    }

    [Fact]
    public void ExplicitOverrideRemovalPersistsWithoutRestoringOldChoice()
    {
        var store = Open();
        store.Save(new ComposeProject
        {
            Name = "project",
            Services = [new() { Name = "worker", Replicas = 2 }],
            ReplicaOverrides = new() { ["worker"] = 3 },
        });
        store = Open();
        var project = store.Get("project")!;
        project.ReplicaOverrides.Clear();
        store.Save(project);
        Assert.Empty(Open().Get("project")!.ReplicaOverrides);
    }

    [Fact]
    public void LegacyOrNullOverridesLoadAsEmpty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "compose-projects.json"),
            """[{"Name":"project","Services":[{"Name":"worker"}],"ReplicaOverrides":null}]""");
        var project = Open().Get("project")!;
        Assert.Empty(project.ReplicaOverrides);
        Assert.Equal(1, Assert.Single(project.Services).Replicas);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
