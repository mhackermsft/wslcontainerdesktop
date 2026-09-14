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

/// <summary>
/// Deploying a template twice produces "name" and "name-2", and the gallery groups those back
/// together so each can be removed individually. Getting the grouping wrong is dangerous in one
/// direction — offering to delete a container that merely looks similar — so the matching is pinned.
/// </summary>
public class TemplateDeploymentNamingTests
{
    [Theory]
    [InlineData("sqlserver", "sqlserver")]
    [InlineData("sqlserver-2", "sqlserver")]
    [InlineData("sqlserver-10", "sqlserver")]
    [InlineData("SQLSERVER-3", "sqlserver")]
    public void RepeatDeploymentsAreRecognized(string candidate, string baseName) =>
        Assert.True(TemplateDeploymentNaming.IsBaseOrSuffixed(candidate, baseName));

    /// <summary>
    /// The digits-only rule is the safety property: a container that merely starts with the same
    /// word belongs to someone else, and treating it as a deployment would offer to delete it.
    /// </summary>
    [Theory]
    [InlineData("sqlserver-backup", "sqlserver")]
    [InlineData("sqlserver-2-old", "sqlserver")]
    [InlineData("sqlserver2", "sqlserver")]
    [InlineData("sqlserverdev", "sqlserver")]
    [InlineData("my-sqlserver", "sqlserver")]
    [InlineData("sqlserver-", "sqlserver")]
    [InlineData("", "sqlserver")]
    [InlineData("sqlserver", "")]
    public void UnrelatedOrAmbiguousNamesAreNotTreatedAsDeployments(string candidate, string baseName) =>
        Assert.False(TemplateDeploymentNaming.IsBaseOrSuffixed(candidate, baseName));

    /// <summary>Plain text ordering would put "-10" before "-2"; the chooser must not.</summary>
    [Fact]
    public void DeploymentsSortNumericallyNotAlphabetically()
    {
        string[] names = ["sqlserver-10", "sqlserver-2", "sqlserver", "sqlserver-3"];

        var ordered = names.OrderBy(n => TemplateDeploymentNaming.SuffixOf(n, "sqlserver")).ToArray();

        Assert.Equal(["sqlserver", "sqlserver-2", "sqlserver-3", "sqlserver-10"], ordered);
    }

    [Fact]
    public void TheBaseNameSortsFirst() =>
        Assert.Equal(1, TemplateDeploymentNaming.SuffixOf("sqlserver", "sqlserver"));

    [Theory]
    [InlineData("sqlserver-backup")]
    [InlineData("unrelated")]
    public void NamesThatAreNotDeploymentsSortLast(string name) =>
        Assert.Equal(int.MaxValue, TemplateDeploymentNaming.SuffixOf(name, "sqlserver"));
}
