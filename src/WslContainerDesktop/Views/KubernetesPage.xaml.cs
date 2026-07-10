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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views;

public sealed partial class KubernetesPage : Page
{
    public KubernetesPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<KubernetesViewModel>();
        InitializeComponent();
        ViewModel.OperationLogUpdated += OnOperationLogUpdated;
    }

    public KubernetesViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UiSafe.Run(() => ViewModel.InitializeAsync());
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.StopPolling();
    }

    private void OnOperationLogUpdated()
    {
        // Auto-scroll the install/uninstall log to the bottom.
        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
    }

    private void HideLog_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowOperationLog = false;
    }

    private void DashboardCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string section)
        {
            ViewModel.SelectedSection = section;
        }
    }

    private void OpenForward_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Models.PortForward pf)
        {
            ViewModel.OpenPortForwardCommand.Execute(pf);
        }
    }

    private void StopForward_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Models.PortForward pf)
        {
            ViewModel.StopPortForwardCommand.Execute(pf);
        }
    }

    // ---- Resource drill-down + quick actions ----

    private static object? RowModel(object sender) => (sender as FrameworkElement)?.DataContext;

    private void Resource_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not null)
        {
            Frame.Navigate(typeof(K8sDetailPage), K8sRef.For(e.ClickedItem));
        }
    }

    private void DeleteResource_Click(object sender, RoutedEventArgs e)
    {
        if (RowModel(sender) is { } model)
        {
            UiSafe.Run(() => ViewModel.DeleteResourceAsync(K8sRef.For(model)));
        }
    }

    private void ScaleDeployment_Click(object sender, RoutedEventArgs e)
    {
        if (RowModel(sender) is { } model)
        {
            UiSafe.Run(() => ViewModel.ScaleDeploymentAsync(K8sRef.For(model)));
        }
    }

    private void RestartDeployment_Click(object sender, RoutedEventArgs e)
    {
        if (RowModel(sender) is { } model)
        {
            UiSafe.Run(() => ViewModel.RestartDeploymentAsync(K8sRef.For(model)));
        }
    }

    private void RunCron_Click(object sender, RoutedEventArgs e)
    {
        if (RowModel(sender) is { } model)
        {
            UiSafe.Run(() => ViewModel.TriggerCronAsync(K8sRef.For(model)));
        }
    }

    private void ToggleCron_Click(object sender, RoutedEventArgs e)
    {
        if (RowModel(sender) is K8sCronJob cron)
        {
            UiSafe.Run(() => ViewModel.SetCronSuspendAsync(K8sRef.For(cron), !cron.Suspended));
        }
    }
}
