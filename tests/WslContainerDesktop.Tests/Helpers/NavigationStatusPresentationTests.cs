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

using System.Xml.Linq;
using Xunit;

namespace WslContainerDesktop.Tests.Helpers;

public sealed class NavigationStatusPresentationTests
{
    [Fact]
    public void SessionSelectionIsNotGlobalAndDashboardUsesAllActiveSessions()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WslContainerDesktop.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root.FullName, "src", "WslContainerDesktop");
        var engine = XDocument.Load(Path.Combine(app, "Views", "WslEnginePage.xaml"));
        var dashboard = XDocument.Load(Path.Combine(app, "Views", "DashboardPage.xaml"));
        var shell = XDocument.Load(Path.Combine(app, "MainWindow.xaml"));
        Assert.DoesNotContain(engine.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "WslcSessionSelector");
        Assert.Contains(engine.Descendants(), element =>
            ((string?)element.Attribute("SelectedItem"))?.Contains("SelectedAssignmentSession", StringComparison.Ordinal) == true);
        Assert.Contains(dashboard.Descendants(), element =>
            ((string?)element.Attribute("Text"))?.Contains("ActiveSessionsDisplay", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(shell.Descendants(), element =>
            ((string?)element.Attribute("Text"))?.Contains("OperationSessionText", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DefaultSessionNotificationsIdentifyTheirScopeAndOpenSessionManagement()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WslContainerDesktop.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root.FullName, "src", "WslContainerDesktop");
        var code = File.ReadAllText(Path.Combine(app, "Services", "NotificationService.cs"));
        Assert.Contains("Notification_DefaultSessionUnavailable", code);
        Assert.Contains("Notification_DefaultSessionRecovered", code);
        Assert.Contains("session could not be reached. Check the WSL engine page for details.\"), \"wsl\"", code);
        Assert.Contains("session is reachable again.\"), \"wsl\"", code);
    }

    [Theory]
    [InlineData("EnginePaneStatusText", "EngineStatusText")]
    [InlineData("KubernetesPaneStatusText", "KubernetesStatusText")]
    public void StatusWrapsWithinThePaneAndRetainsFullTextInTooltip(string controlName, string property)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WslContainerDesktop.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var document = XDocument.Load(Path.Combine(root.FullName, "src", "WslContainerDesktop", "MainWindow.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var navigation = Assert.Single(document.Descendants(), element => element.Name.LocalName == "NavigationView");
        Assert.Equal("240", (string?)navigation.Attribute("OpenPaneLength"));
        var status = Assert.Single(document.Descendants(), element => (string?)element.Attribute(xaml + "Name") == controlName);
        Assert.Equal("Wrap", (string?)status.Attribute("TextWrapping"));
        Assert.Equal("1", (string?)status.Attribute("Grid.Column"));
        Assert.Contains(property, (string?)status.Attribute("Text"));
        if (controlName == "EnginePaneStatusText")
        {
            var button = Assert.Single(status.Ancestors(), element => element.Name.LocalName == "Button");
            Assert.Equal("{x:Bind Shell.SessionStatusDetails, Mode=OneWay}", (string?)button.Attribute("ToolTipService.ToolTip"));
            Assert.Equal("{x:Bind Shell.EngineStatusText, Mode=OneWay}", (string?)button.Attribute("AutomationProperties.Name"));
            Assert.Contains(button.Descendants(), element => element.Name.LocalName == "Flyout");
        }
        else
            Assert.Equal((string?)status.Attribute("Text"), (string?)status.Attribute("ToolTipService.ToolTip"));
        var grid = status.Parent;
        Assert.Equal("Grid", grid?.Name.LocalName);
        Assert.Contains(grid!.Descendants(), element =>
            element.Name.LocalName == "ColumnDefinition" && (string?)element.Attribute("Width") == "*");
    }
}
