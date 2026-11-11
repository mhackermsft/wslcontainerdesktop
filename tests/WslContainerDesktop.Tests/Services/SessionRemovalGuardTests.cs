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

public sealed class SessionRemovalGuardTests
{
    [Fact]
    public void StoppedSessionIgnoresOnlyObservationsStartedBeforeRemovalFinished()
    {
        var root = Path.Combine(Path.GetTempPath(), "wcd-removal-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ManagedSessionStore(NullLogger.Instance, root);
            var owner = AutomationSessionOwner.Capture("engine", new("work"));
            var session = new WslcSessionInfo { ID = 1, CreatorPid = 5, Name = "work" };
            var definition = new ManagedSessionDefinition(Guid.NewGuid(), "work");
            store.Add(definition);
            definition = store.MarkForDeletion(definition, owner);
            var guard = new SessionRemovalGuard(store);
            var oldObservation = DateTimeOffset.UtcNow.AddSeconds(-1);
            guard.ResumeRemoval(owner, null);
            store.Remove(definition);
            guard.Complete(owner, null);
            Assert.True(guard.IsExpectedRemovalObservation(owner, session, oldObservation));
            Assert.False(guard.IsExpectedRemovalObservation(owner, session, DateTimeOffset.UtcNow.AddSeconds(1)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PendingRemovalAndRetiredGenerationRemainOwnerSpecific()
    {
        var root = Path.Combine(Path.GetTempPath(), "wcd-removal-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ManagedSessionStore(NullLogger.Instance, root);
            var definition = new ManagedSessionDefinition(Guid.NewGuid(), "work");
            store.Add(definition);
            var owner = AutomationSessionOwner.Capture("engine", new("work"));
            var other = AutomationSessionOwner.Capture("other-engine", new("work"));
            var generation = new WslcSessionInfo { ID = 1, CreatorPid = 5, Name = "work" };
            definition = store.MarkForDeletion(definition, owner);
            var guard = new SessionRemovalGuard(new ManagedSessionStore(NullLogger.Instance, root));
            Assert.True(guard.IsBlocked(owner, generation, DateTimeOffset.UtcNow));
            Assert.False(guard.IsBlocked(other, generation, DateTimeOffset.UtcNow));
            guard = new SessionRemovalGuard(store);
            guard.ResumeRemoval(owner, generation);
            Assert.True(guard.IsRemovalInProgress(owner));
            Assert.True(guard.IsExpectedRemovalObservation(owner, generation, DateTimeOffset.UtcNow));
            guard.EndRemovalAttempt(owner);
            Assert.False(guard.IsRemovalInProgress(owner));
            Assert.True(guard.IsBlocked(owner, generation, DateTimeOffset.UtcNow));
            Assert.False(guard.IsExpectedRemovalObservation(owner, generation, DateTimeOffset.UtcNow));
            guard.ResumeRemoval(owner, null);
            store.Remove(definition);
            guard.Complete(owner, null);
            Assert.True(guard.IsBlocked(owner, generation, DateTimeOffset.UtcNow.AddSeconds(1)));
            Assert.False(guard.IsBlocked(owner, generation with { ID = 2 }, DateTimeOffset.UtcNow.AddSeconds(1)));
            Assert.False(guard.IsBlocked(other, generation, DateTimeOffset.UtcNow.AddSeconds(1)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void UserStopFencesOnlyItsOwnerUntilResumeAndRejectsLateObservations()
    {
        var root = Path.Combine(Path.GetTempPath(), "wcd-stop-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var guard = new SessionRemovalGuard(new ManagedSessionStore(NullLogger.Instance, root));
            var owner = AutomationSessionOwner.Capture("engine", WslcSessionTarget.Default);
            var other = AutomationSessionOwner.Capture("other", WslcSessionTarget.Default);
            var generation = new WslcSessionInfo { ID = 1, CreatorPid = 5, Name = "default" };
            var old = DateTimeOffset.UtcNow.AddSeconds(-1);
            guard.BeginStop(owner, generation);
            Assert.True(guard.IsBlocked(owner, generation, DateTimeOffset.UtcNow));
            Assert.True(guard.IsExpectedStopObservation(owner, DateTimeOffset.UtcNow));
            Assert.False(guard.IsBlocked(other, generation, DateTimeOffset.UtcNow));
            Assert.False(guard.IsExpectedStopObservation(other, old));

            guard.ReleaseStoppedSession(owner);
            Assert.True(guard.IsResuming(owner));
            Assert.False(guard.IsResuming(other));
            guard.CompleteResumeObservation(owner, old);
            Assert.True(guard.IsResuming(owner));
            Assert.True(guard.IsBlocked(owner, generation, old));
            Assert.True(guard.IsExpectedStopObservation(owner, old));
            Assert.False(guard.IsBlocked(owner, generation, DateTimeOffset.UtcNow.AddSeconds(1)));
            Assert.False(guard.IsExpectedStopObservation(owner, DateTimeOffset.UtcNow.AddSeconds(1)));
            guard.CompleteResumeObservation(owner, DateTimeOffset.UtcNow.AddSeconds(1));
            Assert.False(guard.IsResuming(owner));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
