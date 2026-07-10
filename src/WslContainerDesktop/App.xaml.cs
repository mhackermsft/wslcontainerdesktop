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
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tray;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;
    private StatusMonitor? _monitor;
private ILogger<App>? _logger;
private bool _isExiting;

public App()
{
    InitializeComponent();
    Services = ConfigureServices();

    // Route otherwise-fatal, unobserved failures to the log so a crash leaves a breadcrumb.
    UnhandledException += OnUnhandledException;
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled AppDomain exception (terminating: {Terminating}).", e.IsTerminating);
    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        _logger?.LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    };
}

public new static App Current => (App)Application.Current;

public IServiceProvider Services { get; }

public MainWindow? MainWindow => _window;

/// <summary>True while a real shutdown is in progress (lets the window close for good).</summary>
public bool IsExiting => _isExiting;

private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
{
    _logger?.LogCritical(e.Exception, "Unhandled UI exception: {Message}", e.Message);
}

protected override void OnLaunched(LaunchActivatedEventArgs args)
{
    _logger = Services.GetRequiredService<ILogger<App>>();
    _logger.LogInformation("WSL Container Desktop starting.");

    var settings = Services.GetRequiredService<ISettingsService>();
    settings.Load();

        // The status monitor needs the UI DispatcherQueue, so it is created here and
        // published for the DI container to hand to view models.
        _monitor = new StatusMonitor(
            Services.GetRequiredService<IWslcService>(),
            Services.GetRequiredService<IKubernetesService>(),
            Services.GetRequiredService<RegistryAuthRefresher>(),
            settings,
            DispatcherQueue.GetForCurrentThread(),
            Services.GetRequiredService<ILogger<StatusMonitor>>());
        StatusMonitorAccessor.Instance = _monitor;

        _tray = new TrayIcon();
        _tray.OpenRequested += () => _monitor!.Dispatcher.TryEnqueue(ShowMainWindow);
        _tray.QuitRequested += () => _monitor!.Dispatcher.TryEnqueue(ExitApplication);
        _tray.Initialize();

        _monitor.StatusChanged += OnEngineStatusChanged;
        _monitor.Start();

        _window = new MainWindow();
        _window.ApplyTheme(settings.Theme);

        // When Windows launches us at sign-in (via the StartupTask), start quietly in the
        // tray so we don't steal focus on login; otherwise honor the StartMinimized setting.
        var launchedAtLogin = WasActivatedByStartupTask();

        if (settings.StartMinimized || launchedAtLogin)
        {
            _window.HideToTray();
        }
        else
        {
            _window.Activate();
        }
    }

    private static bool WasActivatedByStartupTask()
    {
        try
        {
            var kind = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent()
                .GetActivatedEventArgs().Kind;
            return kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask;
        }
        catch
        {
            return false;
        }
    }

    private void OnEngineStatusChanged(object? sender, EngineStatusSnapshot e)
    {
        var tooltip = e.Health switch
        {
            EngineHealth.Healthy => $"WSL Container Desktop — {e.Summary}",
            EngineHealth.Down => "WSL Container Desktop — engine unreachable",
            _ => "WSL Container Desktop",
        };

        _tray?.UpdateStatus(e.Health, tooltip, e.Summary);
    }

    public void ShowMainWindow()
    {
        _window ??= new MainWindow();
        _window.ShowFromTray();
    }

    /// <summary>
    /// Brings the app's window forward. Called when a second instance redirects its
    /// activation here. Marshals to the UI thread since the redirect fires on a
    /// background thread.
    /// </summary>
    public void BringToForeground()
    {
        var dispatcher = _monitor?.Dispatcher;
        if (dispatcher is not null)
        {
            dispatcher.TryEnqueue(ShowMainWindow);
        }
        else
        {
            ShowMainWindow();
        }
    }

    public void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _logger?.LogInformation("WSL Container Desktop exiting.");

        // Tear down any running port-forwards so no wsl.exe processes are orphaned.
        try
        {
            Services.GetRequiredService<IKubernetesService>().StopAllPortForwards();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to stop port-forwards during shutdown.");
        }

        _monitor?.Dispose();
        _tray?.Dispose();
        _window?.ForceClose();

        Exit();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Diagnostics: a rolling file log under %LOCALAPPDATA%\WslContainerDesktop\logs plus the
        // debugger output window. This is the single logging seam the rest of the app writes to so
        // previously-swallowed failures leave a breadcrumb. The provider is also registered as a
        // singleton so the Settings page can offer "open logs folder".
#if DEBUG
        var fileLogger = new FileLoggerProvider(LogLevel.Debug);
#else
        var fileLogger = new FileLoggerProvider(LogLevel.Information);
#endif
        services.AddSingleton(fileLogger);
        services.AddLogging(builder =>
        {
#if DEBUG
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
#else
            builder.SetMinimumLevel(LogLevel.Information);
#endif
            builder.AddProvider(fileLogger);
        });

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<IWslcService, WslcService>();
        services.AddSingleton<IKubernetesService, KubernetesService>();
        services.AddSingleton<IAzureCliService, AzureCliService>();
        services.AddSingleton<RegistryAuthRefresher>();
        services.AddSingleton<StartupService>();
        services.AddSingleton<DialogService>();

        services.AddSingleton(_ => StatusMonitorAccessor.Instance
            ?? throw new InvalidOperationException("StatusMonitor not initialized."));

        services.AddSingleton<ContainersViewModel>();
        services.AddSingleton<ImagesViewModel>();
        services.AddSingleton<VolumesViewModel>();
        services.AddSingleton<NetworksViewModel>();
        services.AddSingleton<RegistriesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<KubernetesViewModel>();
        services.AddTransient<K8sDetailViewModel>();

        return services.BuildServiceProvider();
    }
}

/// <summary>
/// Bridges the StatusMonitor (created in OnLaunched because it needs the UI
/// DispatcherQueue) into the DI container.
/// </summary>
internal static class StatusMonitorAccessor
{
    public static StatusMonitor? Instance { get; set; }
}
