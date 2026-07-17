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
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Services;
using WslContainerDesktop.Views;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop;

public sealed partial class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly DialogService _dialogs;

    public MainWindow()
    {
        InitializeComponent();

        Shell = App.Current.Services.GetRequiredService<ShellViewModel>();
        _settings = App.Current.Services.GetRequiredService<ISettingsService>();
        _dialogs = App.Current.Services.GetRequiredService<DialogService>();

        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Title = "WSL Container Desktop";
        CenterAndSize(1200, 780);
        SetMinimumSize(900, 640);

        if (Content is FrameworkElement root)
        {
            root.Loaded += OnRootLoaded;
        }

        AppWindow.Closing += OnAppWindowClosing;

        NavFrame.Navigate(typeof(DashboardPage));
    }

    public ShellViewModel Shell { get; }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        // ContentDialogs need a XamlRoot; publish it once the tree is ready.
        _dialogs.XamlRoot = ((FrameworkElement)sender).XamlRoot;
        RefreshAssistantButtonVisibility();
    }

    private void RefreshAssistantButtonVisibility()
    {
        AssistantButton.Visibility = _settings.AiFeaturesEnabled && _settings.AiProvider != Models.AiProviderKind.None
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AssistantButton_Click(object sender, RoutedEventArgs e)
    {
        AssistantOverlay.Visibility = AssistantOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void AssistantScrim_Click(object sender, RoutedEventArgs e)
    {
        AssistantOverlay.Visibility = Visibility.Collapsed;
    }

    private void AssistantPanel_CloseRequested(object? sender, EventArgs e)
    {
        AssistantOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.Current.IsExiting)
        {
            return;
        }

        if (_settings.CloseToTray)
        {
            args.Cancel = true;
            HideToTray();
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    // Hide the footer status indicators when the pane is collapsed so their dots/labels
    // aren't clipped in the narrow compact rail.
    private void NavView_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        if (PaneFooterPanel is not null)
        {
            PaneFooterPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void NavView_PaneOpening(NavigationView sender, object args)
    {
        if (PaneFooterPanel is not null)
        {
            PaneFooterPanel.Visibility = Visibility.Visible;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "dashboard":
                    NavFrame.Navigate(typeof(DashboardPage));
                    break;
                case "containers":
                    NavFrame.Navigate(typeof(ContainersPage));
                    break;
                case "images":
                    NavFrame.Navigate(typeof(ImagesPage));
                    break;
                case "volumes":
                    NavFrame.Navigate(typeof(VolumesPage));
                    break;
                case "reclaim":
                    NavFrame.Navigate(typeof(ReclaimSpacePage));
                    break;
                case "wsl":
                    NavFrame.Navigate(typeof(WslEnginePage));
                    break;
                case "networks":
                    NavFrame.Navigate(typeof(NetworksPage));
                    break;
                case "endpoints":
                    NavFrame.Navigate(typeof(EndpointsPage));
                    break;
                case "activity":
                    NavFrame.Navigate(typeof(ActivityPage));
                    break;
                case "registries":
                    NavFrame.Navigate(typeof(RegistriesPage));
                    break;
                case "kubernetes":
                    NavFrame.Navigate(typeof(KubernetesPage));
                    break;
                case "compose":
                    NavFrame.Navigate(typeof(ComposePage));
                    break;
                case "templates":
                    NavFrame.Navigate(typeof(TemplatesPage));
                    break;
            }
        }
    }

    public void HideToTray() => AppWindow.Hide();

    /// <summary>Selects the nav item with the given tag, navigating the content frame to it.</summary>
    public void NavigateTo(string tag)
    {
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem nvi && (nvi.Tag as string) == tag)
            {
                NavView.SelectedItem = nvi;
                return;
            }
        }
    }

    /// <summary>
    /// Opens a specific container's detail page (which defaults to the Logs tab), used when the
    /// user clicks the "View logs" button on a container-stopped toast. Falls back to the
    /// Containers list if the container is no longer listed.
    /// </summary>
    public void OpenContainerLogs(string containerId)
    {
        // Route to the Containers page first so the detail page has a valid back stack.
        NavigateTo("containers");

        if (string.IsNullOrEmpty(containerId))
        {
            return;
        }

        var vm = App.Current.Services.GetRequiredService<ContainersViewModel>();
        var row = vm.Containers.FirstOrDefault(c => string.Equals(c.Id, containerId, StringComparison.Ordinal));
        if (row is null)
        {
            return;
        }

        vm.Selected = row;
        NavFrame.Navigate(typeof(ContainerDetailPage));
    }

    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = true;
            presenter.Restore();
        }

        ForceForeground();
    }

    /// <summary>
    /// Reliably brings the window to the foreground, even when the caller is not the
    /// current foreground process (Windows normally blocks SetForegroundWindow in that
    /// case). Uses the AttachThreadInput workaround.
    /// </summary>
    private void ForceForeground()
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        var foreground = Helpers.NativeMethods.GetForegroundWindow();
        var foreThread = Helpers.NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var thisThread = Helpers.NativeMethods.GetCurrentThreadId();

        if (foreThread != thisThread)
        {
            Helpers.NativeMethods.AttachThreadInput(thisThread, foreThread, true);
            Helpers.NativeMethods.BringWindowToTop(hwnd);
            Helpers.NativeMethods.SetForegroundWindow(hwnd);
            Helpers.NativeMethods.AttachThreadInput(thisThread, foreThread, false);
        }
        else
        {
            Helpers.NativeMethods.BringWindowToTop(hwnd);
            Helpers.NativeMethods.SetForegroundWindow(hwnd);
        }
    }

    public void ForceClose() => Close();

    private void SetMinimumSize(int logicalWidth, int logicalHeight)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var dpi = Helpers.NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;

        presenter.PreferredMinimumWidth = (int)(logicalWidth * scale);
        presenter.PreferredMinimumHeight = (int)(logicalHeight * scale);
    }

    private void CenterAndSize(int logicalWidth, int logicalHeight)
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var dpi = Helpers.NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;

        var width = (int)(logicalWidth * scale);
        var height = (int)(logicalHeight * scale);

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);

        var x = work.X + ((work.Width - width) / 2);
        var y = work.Y + ((work.Height - height) / 2);

        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }

    public void ApplyTheme(string theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }
}
