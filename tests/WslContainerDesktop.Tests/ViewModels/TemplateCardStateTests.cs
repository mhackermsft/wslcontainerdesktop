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

using WslContainerDesktop.Models;
using Xunit;

namespace WslContainerDesktop.Tests.ViewModels;

/// <summary>
/// What a template card offers once it has been deployed. Launch used to be disabled here, which
/// left the gallery unable to reach its own multi-deployment support: the count badge and the
/// "which one?" remove chooser could only ever be produced by the assistant.
/// </summary>
public class TemplateCardStateTests
{
    private static StackTemplate Template() => new()
    {
        Id = "mysql",
        Name = "MySQL",
        Description = "MySQL 8 relational database on port 3306.",
        Category = "Databases",
    };

    /// <summary>
    /// A repeat launch steps aside onto its own name, ports and volumes, so it costs the deployment
    /// already running nothing and does not need to be blocked.
    /// </summary>
    [Fact]
    public void DeployedTemplatesCanStillBeLaunchedAgain()
    {
        var template = Template();
        template.IsDeployed = true;
        template.DeploymentCount = 1;

        Assert.True(template.CanLaunch);
        // One deployment is the ordinary case and says nothing extra.
        Assert.False(template.HasMultipleDeployments);
        Assert.Equal("", template.DeploymentSummary);
    }

    [Fact]
    public void ASecondDeploymentIsAnnouncedOnTheCard()
    {
        var template = Template();
        template.IsDeployed = true;
        template.DeploymentCount = 2;

        Assert.True(template.CanLaunch);
        Assert.True(template.HasMultipleDeployments);
        Assert.Equal("2 deployments", template.DeploymentSummary);
    }

    /// <summary>A launch already in flight still disables the button, or a double click deploys twice.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ABusyCardCannotBeLaunched(bool launching, bool removing)
    {
        var template = Template();
        template.IsLaunching = launching;
        template.IsRemoving = removing;

        Assert.True(template.IsBusyCard);
        Assert.False(template.CanLaunch);
    }
}
