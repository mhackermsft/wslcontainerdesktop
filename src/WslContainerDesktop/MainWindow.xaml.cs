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
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Services;
using WslContainerDesktop.Views;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop;

/// <summary>
/// Main WinUI shell window that hosts the navigation view, pages, update bar, requirement gate,
/// and assistant overlay for the app.
/// </summary>
/// <remarks>
/// The code-behind stays thin: it resolves view models from DI, maps navigation tags to pages,
/// and handles window-specific concerns such as close-to-tray and foreground activation.
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly IAiAvailabilityService _aiAvailability;
    private readonly IWslRequirementService _requirements;
    private readonly RequirementGateViewModel _gate;
    private readonly WindowVisibility _visibility;
    private string _currentTag = "dashboard";
    private string _effectiveLanguage = string.Empty;

    private static readonly HashSet<string> GatedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "dashboard",
        "containers",
        "images",
        "volumes",
        "networks",
        "endpoints",
        "activity",
        "registries",
        "compose",
        "devcontainers",
        "templates",
        "reclaim",
    };

    /// <summary>Creates the shell, resolves shared view models, and initializes navigation state.</summary>
    public MainWindow()
    {
        InitializeComponent();

        Shell = App.Current.Services.GetRequiredService<ShellViewModel>();
        Updates = App.Current.Services.GetRequiredService<AppUpdateViewModel>();
        _settings = App.Current.Services.GetRequiredService<ISettingsService>();
        _dialogs = App.Current.Services.GetRequiredService<DialogService>();
        _aiAvailability = App.Current.Services.GetRequiredService<IAiAvailabilityService>();
        _requirements = App.Current.Services.GetRequiredService<IWslRequirementService>();
        _gate = App.Current.Services.GetRequiredService<RequirementGateViewModel>();
        _visibility = App.Current.Services.GetRequiredService<WindowVisibility>();

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
        AppWindow.Changed += OnAppWindowChanged;
        try
        {
            global::Microsoft.Windows.System.Power.PowerManager.DisplayStatusChanged += OnDisplayStatusChanged;
            Closed += (_, _) => global::Microsoft.Windows.System.Power.PowerManager.DisplayStatusChanged -= OnDisplayStatusChanged;
        }
        catch (Exception ex)
        {
            // Without display notifications polling simply isn't paused while the display is off.
            App.Current.Services.GetService<ILoggerFactory>()?.CreateLogger<MainWindow>()
                .LogWarning(ex, "Display power notifications are unavailable.");
        }

        PublishWindowState();
        PublishDisplayStatus();
        _settings.Changed += OnSettingsChanged;
        _aiAvailability.Changed += OnAiAvailabilityChanged;
        _requirements.Changed += OnRequirementChanged;
        _gate.OpenSettingsRequested += OnGateOpenSettingsRequested;
        Activated += OnWindowActivated;

        NavFrame.NavigateWithPreference(typeof(DashboardPage));
        RefreshRequirementGate();
    }

    /// <summary>View model for status indicators and shell-level state shown outside individual pages.</summary>
    public ShellViewModel Shell { get; }

    /// <summary>View model for the in-app update banner and update actions in the shell.</summary>
    public AppUpdateViewModel Updates { get; }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated || AppLanguage.Normalize(_settings.Language) != AppLanguage.SystemDefault)
            return;
        try { ApplyLanguage(_settings.Language, refreshUi: true); }
        catch (Exception ex)
        {
            App.Current.Services.GetService<ILoggerFactory>()?.CreateLogger<MainWindow>()
                .LogWarning(ex, "Could not refresh the Windows display language.");
        }
    }

    /// <summary>Converts a Boolean into WinUI <see cref="Visibility"/> for <c>x:Bind</c> expressions.</summary>
    public static Visibility ToVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateBar_CloseButtonClick(InfoBar sender, object args) => Updates.DismissBar();

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        // ContentDialogs need a XamlRoot; publish it once the tree is ready.
        _dialogs.XamlRoot = ((FrameworkElement)sender).XamlRoot;
        RefreshAssistantButtonVisibility();
        RefreshRequirementGate();
        ApplyAnimationPreference();
    }

    private void RefreshAssistantButtonVisibility()
    {
        AssistantButton.Visibility = _settings.AiFeaturesEnabled
            && _settings.AiProvider != Models.AiProviderKind.None
            && _aiAvailability.CanUseTools
            && _requirements.Current.State == WslRequirementState.Ok
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnAiAvailabilityChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshAssistantButtonVisibility();

            if (AssistantButton.Visibility == Visibility.Collapsed && AssistantOverlay.Visibility == Visibility.Visible)
            {
                AssistantOverlay.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyAnimationPreference();
            RefreshAssistantButtonVisibility();

            // If AI was turned off while the panel was open, close it too.
            if (AssistantButton.Visibility == Visibility.Collapsed && AssistantOverlay.Visibility == Visibility.Visible)
            {
                AssistantOverlay.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void OnRequirementChanged(object? sender, WslRequirementStatus e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshAssistantButtonVisibility();
            RefreshRequirementGate();
        });
    }

    private void OnGateOpenSettingsRequested(object? sender, EventArgs e)
    {
        _currentTag = "settings";
        NavFrame.NavigateWithPreference(typeof(SettingsPage));
        NavView.SelectedItem = null;
        RefreshRequirementGate();
    }

    private void AssistantButton_Click(object sender, RoutedEventArgs e)
    {
        if (AssistantOverlay.Visibility != Visibility.Visible)
        {
            AssistantPanel.ViewModel.RefreshProviderLabel();
            AssistantPanel.ViewModel.BeginRefreshAvailability();
        }

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

    // Restoring from minimized raises only size/position changes, so republish on every change;
    // WindowVisibility raises Changed only when viewability actually flips.
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => PublishWindowState();

    // Lets UI-only polling pause while the window is hidden in the tray or minimized.
    private void PublishWindowState() => _visibility.SetWindowState(
        AppWindow.IsVisible,
        AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized });

    private void OnDisplayStatusChanged(object? sender, object e) => PublishDisplayStatus();

    // Raised on a background thread; WindowVisibility is thread-safe.
    private void PublishDisplayStatus()
    {
        try
        {
            _visibility.SetDisplayOn(
                global::Microsoft.Windows.System.Power.PowerManager.DisplayStatus != global::Microsoft.Windows.System.Power.DisplayStatus.Off);
        }
        catch (Exception ex)
        {
            // Treat the display as on, so visible UI keeps updating.
            App.Current.Services.GetService<ILoggerFactory>()?.CreateLogger<MainWindow>()
                .LogDebug(ex, "Reading the display power state failed.");
            _visibility.SetDisplayOn(true);
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

    private bool _navIndicatorHidden;
    private bool? _appliedAnimations;

    /// <summary>Hides or restores the nav pane's sliding selection indicator to match the animations setting.</summary>
    private void ApplyNavIndicatorPreference()
    {
        var animate = _settings.PageAnimations;
        if (!animate || _navIndicatorHidden)
        {
            FrameNavigationExtensions.SetNavIndicatorVisible(NavView, animate);
            _navIndicatorHidden = !animate;
        }
    }

    /// <summary>Applies the animations setting to the shell and the current page (used when the setting changes).</summary>
    private void ApplyAnimationPreference()
    {
        // Every settings save raises Changed; only re-walk the tree when this setting actually flipped.
        if (_appliedAnimations == _settings.PageAnimations)
        {
            return;
        }

        _appliedAnimations = _settings.PageAnimations;
        ApplyNavIndicatorPreference();
        if (NavFrame.Content is DependencyObject page)
        {
            FrameNavigationExtensions.ApplyItemTransitions(page, _settings.PageAnimations);
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // The indicator is (re)created as selection moves; re-hide it once layout has caught up.
        if (!_settings.PageAnimations)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyNavIndicatorPreference);
        }

        if (args.IsSettingsSelected)
        {
            _currentTag = "settings";
            NavFrame.NavigateWithPreference(typeof(SettingsPage));
            RefreshRequirementGate();
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            _currentTag = item.Tag as string ?? string.Empty;
            if (PageTypeFor(_currentTag) is { } pageType)
            {
                NavFrame.NavigateWithPreference(pageType);
            }

            RefreshRequirementGate();
        }
    }

    /// <summary>Maps a navigation item's Tag to the page it opens, or null for an unknown tag.</summary>
    private static Type? PageTypeFor(string tag) => tag switch
    {
        "dashboard" => typeof(DashboardPage),
        "containers" => typeof(ContainersPage),
        "images" => typeof(ImagesPage),
        "volumes" => typeof(VolumesPage),
        "reclaim" => typeof(ReclaimSpacePage),
        "wsl" => typeof(WslEnginePage),
        "networks" => typeof(NetworksPage),
        "endpoints" => typeof(EndpointsPage),
        "activity" => typeof(ActivityPage),
        "registries" => typeof(RegistriesPage),
        "kubernetes" => typeof(KubernetesPage),
        "compose" => typeof(ComposePage),
        "devcontainers" => typeof(DevContainersPage),
        "templates" => typeof(TemplatesPage),
        _ => null,
    };

    /// <summary>
    /// Returns to a section's main page when its already-selected nav item is clicked again, for
    /// example going from a container's detail page back to the Containers list. SelectionChanged
    /// does not fire in that case because the selection itself has not changed.
    /// </summary>
    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item
            && ReferenceEquals(item, sender.SelectedItem)
            && item.Tag is string tag
            && PageTypeFor(tag) is { } pageType
            && NavFrame.Content?.GetType() != pageType)
        {
            NavFrame.NavigateWithPreference(pageType);
        }
    }

    /// <summary>Hides the window while leaving the tray icon and background monitoring alive.</summary>
    public void HideToTray() => AppWindow.Hide();

    /// <summary>Selects the nav item with the given tag, navigating the content frame to it.</summary>
    public void NavigateTo(string tag)
    {
        foreach (var item in NavView.MenuItems.Concat(NavView.FooterMenuItems))
        {
            if (item is NavigationViewItem nvi && (nvi.Tag as string) == tag)
            {
                NavView.SelectedItem = nvi;
                return;
            }
        }
    }

    private void RefreshRequirementGate()
    {
        // Until the first version check finishes the state is merely "not checked yet", so the gate
        // stays hidden instead of flashing the "required" screen at every launch.
        var gated = _requirements.HasCompletedInitialCheck && _requirements.Current.State != WslRequirementState.Ok;
        foreach (var item in NavView.MenuItems.Concat(NavView.FooterMenuItems))
        {
            if (item is NavigationViewItem nvi && nvi.Tag is string tag)
            {
                nvi.IsEnabled = !gated || !GatedTags.Contains(tag) || tag == "dashboard";
            }
        }

        RequirementGate.Visibility = gated && GatedTags.Contains(_currentTag)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Opens a specific container's detail page (which defaults to the Logs tab), used when the
    /// user clicks the "View logs" button on a container-stopped toast. Falls back to the
    /// Containers list if the container is no longer listed.
    /// </summary>
    public void OpenContainerLogs(string containerId, string? containerName = null)
    {
        // Route to the Containers page first so the detail page has a valid back stack.
        NavigateTo("containers");

        if (string.IsNullOrEmpty(containerId) && string.IsNullOrWhiteSpace(containerName))
        {
            return;
        }

        var vm = App.Current.Services.GetRequiredService<ContainersViewModel>();
        var row = vm.Containers.FirstOrDefault(c =>
            !string.IsNullOrEmpty(containerId) &&
            (c.Id.StartsWith(containerId, StringComparison.OrdinalIgnoreCase) ||
             containerId.StartsWith(c.Id, StringComparison.OrdinalIgnoreCase)));
        if (row is null && !string.IsNullOrWhiteSpace(containerName))
        {
            row = vm.Containers.FirstOrDefault(c =>
                string.Equals(c.Name, containerName.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
        }

        if (row is null)
        {
            return;
        }

        vm.Selected = row;
        NavFrame.NavigateWithPreference(typeof(ContainerDetailPage));
    }

    /// <summary>Restores and foregrounds the window after the user opens the tray icon.</summary>
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

    /// <summary>Closes the WinUI window during real application shutdown, bypassing close-to-tray behavior.</summary>
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

    /// <summary>Applies the saved light, dark, or system theme to the shell root element.</summary>
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

    /// <summary>
    /// Applies the app-wide resource language and pins the shell root's inherited language.
    /// </summary>
    /// <remarks>
    /// An empty preference means "follow the system". Resolve it before either native property is
    /// written; an empty/null HSTRING is rejected. The saved preference itself stays unchanged.
    /// </remarks>
    /// <param name="languageTag">BCP-47 tag, or empty to follow the system.</param>
    /// <param name="refreshUi">
    /// Whether to refresh shell bindings after a runtime switch without discarding the loaded page.
    /// False at startup, before the window is shown.
    /// </param>
    public void ApplyLanguage(string languageTag, bool refreshUi = false)
    {
        var tag = App.ApplyPrimaryLanguageOverride(languageTag);
        var changed = tag != _effectiveLanguage;
        _effectiveLanguage = tag;
        App.Current.Services.GetRequiredService<ITextLocalizer>().SetLanguage(tag);
        UiText.SetLanguage(tag);
        if (Content is not FrameworkElement root)
        {
            return;
        }

        try
        {
            // The storage sentinel is never assigned to this WinRT property.
            root.Language = tag;
        }
        catch (Exception ex)
        {
            App.Current.Services.GetService<ILoggerFactory>()?.CreateLogger<MainWindow>()
                .LogWarning(ex, "Could not apply the effective UI language {Language}.", tag);
            return;
        }

        if (changed)
            UiText.NotifyLanguageChanged();
        if (refreshUi && changed)
        {
            RefreshForLanguage();
        }
    }

    /// <summary>
    /// Updates shell bindings; Localization.Uid refreshes resource properties on existing controls.
    /// </summary>
    private void RefreshForLanguage()
    {
        try
        {
            RefreshShellText();

            // Localization.Uid reapplies static resource properties on existing controls. Keeping
            // the view alive preserves passwords, drafts, navigation parameters and active streams.
            Bindings.Update();

            RefreshRequirementGate();
            RefreshAssistantButtonVisibility();
        }
        catch (Exception ex)
        {
            App.Current.Services.GetService<ILoggerFactory>()?.CreateLogger<MainWindow>()
                .LogWarning(ex, "Could not rebuild the UI after switching language.");
        }
    }

    private void RefreshShellText()
    {
        var text = App.Current.Services.GetRequiredService<ITextLocalizer>();
        foreach (var item in NavView.MenuItems.Concat(NavView.FooterMenuItems))
        {
            if (item is NavigationViewItem { Tag: string tag } navItem)
            {
                var key = tag switch
                {
                    "wsl" => "Nav_WslEngine.Content",
                    "reclaim" => "Nav_Reclaim.Content",
                    "devcontainers" => "Nav_DevContainers.Content",
                    _ => $"Nav_{char.ToUpperInvariant(tag[0])}{tag[1..]}.Content",
                };
                navItem.Content = text.Get(key);
            }
        }

        if (NavView.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = text.Get("Nav_Settings.Content");
        }

        AssistantLabel.Text = text.Get("Nav_Assistant.Text");
        WhatsNewButton.Content = text.Get("Update_WhatsNew.Content");
    }
}
